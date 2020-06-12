// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MS-PL license. See LICENSE.txt file in the project root for full license information.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeGeneration.Roslyn.Engine
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using McMaster.NETCore.Plugins;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Text;

    /// <summary>
    /// Runs code generation for every applicable document and handles resulting syntax trees,
    /// saving them to <see cref="IntermediateOutputDirectory"/>.
    /// </summary>
    public class CompilationGenerator
    {
        private const int ProcessCannotAccessFileHR = unchecked((int)0x80070020);

        private readonly Type[] pluginSharedTypes = new[] { typeof(ICodeGenerator), typeof(Compilation), typeof(CSharpCompilation) };
        private readonly List<string> emptyGeneratedFiles = new List<string>();
        private readonly List<string> generatedFiles = new List<string>();
        private readonly List<string> additionalWrittenFiles = new List<string>();
        private readonly List<string> loadedAssemblies = new List<string>();
        private readonly Dictionary<string, (PluginLoader Loader, Assembly Assembly)> cachedPlugins = new Dictionary<string, (PluginLoader, Assembly)>(StringComparer.OrdinalIgnoreCase);
        private readonly string _fileLastModifiedPath;
        private readonly Dictionary<string, DateTime> fileLastModifiedDates;
        private readonly DateTime assembliesLastModified;

        private const string GeneratedFileSuffix = ".generated.cs";

        /// <summary>
        /// Initializes a new instance of the <see cref="CompilationGenerator"/> class.
        /// </summary>
        /// <param name="projectDirectory">The directory with the project file.</param>
        /// <param name="compile">The list of paths of files to be compiled.</param>
        /// <param name="referencePath">The list of paths to reference assemblies.</param>
        /// <param name="preprocessorSymbols">A set of preprocessor symbols to define.</param>
        /// <param name="pluginPaths">The paths to plugins.</param>
        /// <param name="intermediateOutputDirectory">The path to the directory that contains generated source files.</param>
        /// <param name="fileLastModifiedPath"></param>
        /// <param name="buildProperties">The build properties to expose to generators.</param>
        public CompilationGenerator(string? projectDirectory, IReadOnlyList<string> compile,
            IReadOnlyList<string> referencePath, IEnumerable<string> preprocessorSymbols,
            IReadOnlyList<string> pluginPaths, string intermediateOutputDirectory, string fileLastModifiedPath,
            IReadOnlyDictionary<string, string> buildProperties)
        {
            ProjectDirectory = projectDirectory;
            Compile = compile ?? throw new ArgumentNullException(nameof(compile));
            ReferencePath = referencePath ?? throw new ArgumentNullException(nameof(referencePath));
            PreprocessorSymbols = preprocessorSymbols ?? throw new ArgumentNullException(nameof(preprocessorSymbols));
            PluginPaths = pluginPaths ?? throw new ArgumentNullException(nameof(pluginPaths));
            IntermediateOutputDirectory = intermediateOutputDirectory ?? throw new ArgumentNullException(nameof(intermediateOutputDirectory));
            BuildProperties = buildProperties ?? throw new ArgumentNullException(nameof(buildProperties));

            _fileLastModifiedPath = fileLastModifiedPath ?? throw new ArgumentNullException(nameof(fileLastModifiedPath));
            fileLastModifiedDates = ReadFileLastModifiedDateDictionary();
            assembliesLastModified = GetLastModifiedAssemblyTime();
        }

        /// <summary>
        /// Gets the list of paths of files to be compiled.
        /// </summary>
        public IReadOnlyList<string> Compile { get; }

        /// <summary>
        /// Gets the list of paths to reference assemblies.
        /// </summary>
        public IReadOnlyList<string> ReferencePath { get; }

        /// <summary>
        /// Gets a set of preprocessor symbols to define.
        /// </summary>
        public IEnumerable<string> PreprocessorSymbols { get; }

        /// <summary>
        /// Gets the build properties to expose to generators.
        /// </summary>
        public IReadOnlyDictionary<string, string> BuildProperties { get; }

        /// <summary>
        /// Gets the paths to plugins.
        /// </summary>
        public IReadOnlyList<string> PluginPaths { get; }

        /// <summary>
        /// Gets the path to the directory that contains generated source files.
        /// </summary>
        public string IntermediateOutputDirectory { get; }

        /// <summary>
        /// Gets the set of files generated after <see cref="GenerateAsync"/> is invoked.
        /// </summary>
        public IEnumerable<string> GeneratedFiles => this.generatedFiles;

        /// <summary>
        /// Gets the set of files written in addition to those found in <see cref="GeneratedFiles"/>.
        /// </summary>
        public IEnumerable<string> AdditionalWrittenFiles => this.additionalWrittenFiles;

        /// <summary>
        /// Gets the subset of <see cref="GeneratedFiles"/> that contain no types.
        /// </summary>
        public IEnumerable<string> EmptyGeneratedFiles => this.emptyGeneratedFiles;

        /// <summary>
        /// Gets the directory with the project file.
        /// </summary>
        public string? ProjectDirectory { get; }

        private Dictionary<string, DateTime> ReadFileLastModifiedDateDictionary()
        {
            if (!File.Exists(_fileLastModifiedPath))
            {
                return new Dictionary<string, DateTime>();
            }

            string json = File.ReadAllText(_fileLastModifiedPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);

            return dict;
        }

        private void WriteFileLastModifiedDateDictionary()
        {
            File.WriteAllText(_fileLastModifiedPath, JsonSerializer.Serialize(fileLastModifiedDates));
        }

        private DateTime GetFileLastWriteTime(string fileName)
        {
            if (fileLastModifiedDates.TryGetValue(fileName, out DateTime lastWriteTime))
            {
                return lastWriteTime;
            }

            return DateTime.MinValue;
        }

        private void UpdateFileLastWriteTime(string fileName)
        {
            fileLastModifiedDates[fileName] = DateTime.Now;
        }

        private bool IsFileModified(SHA1 hasher, string filePath, out string outputFileName)
        {
            string sourceHash = Convert.ToBase64String(hasher.ComputeHash(Encoding.UTF8.GetBytes(filePath)), 0, 6).Replace('/', '-');
            Logger.Info($"File \"{filePath}\" hashed to {sourceHash}");

            outputFileName = Path.GetFileNameWithoutExtension(filePath) + $".{sourceHash}{GeneratedFileSuffix}";

            DateTime outputLastModified = GetFileLastWriteTime(outputFileName);

            bool isFileModified = File.GetLastWriteTime(filePath) > outputLastModified;
            bool isAssemblyModified = assembliesLastModified > outputLastModified;

            return isFileModified || isAssemblyModified;
        }

        private async Task<(bool result, string outputFileName)> ShouldGenerateFile(SHA1 hasher, CSharpCompilation compilation, SyntaxTree inputSyntaxTree, CancellationToken cancellationToken)
        {
            if (inputSyntaxTree.FilePath.EndsWith(GeneratedFileSuffix))
            {
               Logger.Info($"File \"{inputSyntaxTree.FilePath}\" is a generated file (ends with '{GeneratedFileSuffix}'). Skipping generation for this file.");
               return (false, string.Empty);
            }

            if (!IsFileModified(hasher, inputSyntaxTree.FilePath, out string outputFileName))
            {
                Logger.Info($"File \"{inputSyntaxTree.FilePath}\" is not modified. Skipping generation for this file.");
                return (false, outputFileName);
            }

            // file will or won't be generated, but we store the current time as it has been processed
            UpdateFileLastWriteTime(outputFileName);

            if (!await DocumentTransform.HasCodeGenerators(compilation, inputSyntaxTree, cancellationToken))
            {
                Logger.Info($"No code generators found for \"{inputSyntaxTree.FilePath}\". Skipping generation for this file.");
                return (false, outputFileName);
            }

            return (true, outputFileName);
        }

        /// <summary>
        /// Runs the code generation as configured using this instance's properties.
        /// </summary>
        /// <param name="progress">Optional handler of diagnostics provided by code generator.</param>
        /// <param name="cancellationToken">Cancellation token to interrupt async operations.</param>
        /// <returns>A <see cref="Task.CompletedTask"/>.</returns>
        public async Task GenerateAsync(IProgress<Diagnostic> progress, CancellationToken cancellationToken = default)
        {
            if (progress is null)
            {
                throw new ArgumentNullException(nameof(progress));
            }

            var compilation = this.CreateCompilation(cancellationToken);

            var fileFailures = new List<Exception>();

            using (var hasher = System.Security.Cryptography.SHA1.Create())
            {
                foreach (var inputSyntaxTree in compilation.SyntaxTrees)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Logger.Info("Started processing file " + inputSyntaxTree.FilePath);
                    //Logger.Info("InputSyntaxTree:" + Environment.NewLine + inputSyntaxTree);

                    var shouldGen = await ShouldGenerateFile(hasher, compilation, inputSyntaxTree, cancellationToken);
                    if (!shouldGen.result)
                    {
                        continue;
                    }

                    string outputFilePath = Path.Combine(this.IntermediateOutputDirectory, shouldGen.outputFileName);
                    Logger.Info("File output will be: " + outputFilePath);

                    int retriesLeft = 3;
                    do
                    {
                        try
                        {
                            var generatedSyntaxTree = await DocumentTransform.TransformAsync(
                                compilation,
                                inputSyntaxTree,
                                this.ProjectDirectory,
                                this.BuildProperties,
                                this.LoadPlugin,
                                progress,
                                cancellationToken);

                            var outputText = await generatedSyntaxTree.GetTextAsync(cancellationToken);

                            using (var outputFileStream = File.OpenWrite(outputFilePath))
                            using (var outputWriter = new StreamWriter(outputFileStream))
                            {
                                outputText.Write(outputWriter, cancellationToken);

                                // Truncate any data that may be beyond this point if the file existed previously.
                                outputWriter.Flush();
                                outputFileStream.SetLength(outputFileStream.Position);
                            }

                            if (!(generatedSyntaxTree is null))
                            {
                                var root = await generatedSyntaxTree.GetRootAsync(cancellationToken);
                                bool anyTypesGenerated = root.DescendantNodes().OfType<TypeDeclarationSyntax>().Any();
                                if (!anyTypesGenerated)
                                {
                                    this.emptyGeneratedFiles.Add(outputFilePath);
                                }
                            }
                            break;
                        }
                        catch (IOException ex) when (ex.HResult == ProcessCannotAccessFileHR && retriesLeft > 0)
                        {
                            retriesLeft--;
                            await Task.Delay(200, cancellationToken);
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            ReportError(progress, "CGR001", inputSyntaxTree, ex);
                            fileFailures.Add(ex);
                            break;
                        }
                    }
                    while (true);

                    this.generatedFiles.Add(outputFilePath);
                }
            }

            WriteFileLastModifiedDateDictionary();

            if (fileFailures.Count > 0)
            {
                throw new AggregateException(fileFailures);
            }
        }

        private Assembly? LoadPlugin(AssemblyName assemblyName)
        {
            if (cachedPlugins.TryGetValue(assemblyName.Name, out var cached))
            {
                Logger.Info($"CGR retrieved cached plugin for {assemblyName.Name}: {cached.Assembly.Location}");
                return cached.Assembly;
            }
            Logger.Info($"CGR looking up plugin {assemblyName.Name}");
            var pluginPath = PluginPaths.FirstOrDefault(IsRequestedPlugin);
            if (pluginPath is null)
            {
                Logger.Info($"CGR didn't find plugin for {assemblyName.Name}");
                return null;
            }
            Logger.Info($"CGR loading up plugin {assemblyName.Name} from {pluginPath}");
            var loader = PluginLoader.CreateFromAssemblyFile(pluginPath, pluginSharedTypes);
            var assembly = loader.LoadDefaultAssembly();
            cachedPlugins[assemblyName.Name] = (loader, assembly);
            this.loadedAssemblies.Add(pluginPath);
            Logger.Info($"CGR loaded plugin for {assemblyName.Name}: {assembly.Location}");
            return assembly;

            bool IsRequestedPlugin(string path)
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                return string.Equals(assemblyName.Name, fileName, StringComparison.OrdinalIgnoreCase);
            }
        }

        private DateTime GetLastModifiedAssemblyTime()
        {
            var timestamps =
                from path in PluginPaths
                where File.Exists(path)
                select File.GetLastWriteTime(path);
            return timestamps.DefaultIfEmpty().Max();
        }

        private static void ReportError(IProgress<Diagnostic> progress, string id, SyntaxTree inputSyntaxTree, Exception ex)
        {
            Console.Error.WriteLine($"Exception in file processing: {ex}");

            if (progress == null)
            {
                return;
            }

            const string category = "CodeGen.Roslyn: Transformation";
            const string messageFormat = "{0}";

            var descriptor = new DiagnosticDescriptor(
                id,
                "Error during transformation",
                messageFormat,
                category,
                DiagnosticSeverity.Error,
                true);

            var location = inputSyntaxTree != null ? Location.Create(inputSyntaxTree, TextSpan.FromBounds(0, 0)) : Location.None;

            var messageArgs = new object[]
            {
                ex,
            };

            var reportDiagnostic = Diagnostic.Create(descriptor, location, messageArgs);

            progress.Report(reportDiagnostic);
        }

        private CSharpCompilation CreateCompilation(CancellationToken cancellationToken)
        {
            var compilation = CSharpCompilation.Create("codegen")
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithReferences(this.ReferencePath.Select(p => MetadataReference.CreateFromFile(p)));
            var parseOptions = new CSharpParseOptions(preprocessorSymbols: this.PreprocessorSymbols);

            foreach (var sourceFile in this.Compile)
            {
                using (var stream = File.OpenRead(sourceFile))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = SourceText.From(stream);
                    compilation = compilation.AddSyntaxTrees(
                        CSharpSyntaxTree.ParseText(
                            text,
                            parseOptions,
                            sourceFile,
                            cancellationToken));
                }
            }

            return compilation;
        }
    }
}
