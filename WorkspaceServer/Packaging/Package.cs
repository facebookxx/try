﻿using System;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using Buildalyzer;
using Buildalyzer.Workspaces;
using Clockwise;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using MLS.Agent.Tools;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Pocket;
using WorkspaceServer.Servers.Roslyn;
using static Pocket.Logger<WorkspaceServer.Packaging.Package>;
using Disposable = System.Reactive.Disposables.Disposable;

namespace WorkspaceServer.Packaging
{
    public abstract class Package
    {
        const string CSharpLanguageVersion = "7.3";
        protected const string DesignTimeBuildBinlogFileName = "designTimeBuild.binlog";

        static Package()
        {
            const string workspacesPathEnvironmentVariableName = "TRYDOTNET_PACKAGES_PATH";

            var environmentVariable = Environment.GetEnvironmentVariable(workspacesPathEnvironmentVariableName);

            DefaultPackagesDirectory =
                environmentVariable != null
                    ? new DirectoryInfo(environmentVariable)
                    : new DirectoryInfo(
                        Path.Combine(
                            Paths.UserProfile,
                            ".trydotnet",
                            "packages"));

            if (!DefaultPackagesDirectory.Exists)
            {
                DefaultPackagesDirectory.Create();
            }

            Log.Info("Packages path is {DefaultWorkspacesDirectory}", DefaultPackagesDirectory);
        }

        private readonly IPackageInitializer _initializer;
        private static readonly object _lockObj = new object();
        private bool? _isWebProject;
        private bool? _isUnitTestProject;
        private FileInfo _entryPointAssemblyPath;
        private static string _targetFramework;
        private readonly Logger _log;
        private readonly Subject<Budget> _fullBuildRequestChannel;

        private readonly IScheduler _buildThrottleScheduler;
        private readonly SerialDisposable _fullBuildThrottlerSubscription;
        private readonly AsyncLazy<bool> _lazyCreation;

        private readonly SemaphoreSlim _buildSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _publishSemaphore = new SemaphoreSlim(1, 1);
        private readonly Subject<Budget> _designTimeBuildRequestChannel;
        private readonly SerialDisposable _designTimeBuildThrottlerSubscription;

        private TaskCompletionSource<Workspace> _fullBuildCompletionSource = new TaskCompletionSource<Workspace>();
        private TaskCompletionSource<Workspace> _designTimeBuildCompletionSource = new TaskCompletionSource<Workspace>();

        private readonly object _fullBuildCompletionSourceLock = new object();
        private readonly object _designTimeBuildCompletionSourceLock = new object();

        protected Package(
            string name = null,
            IPackageInitializer initializer = null,
            DirectoryInfo directory = null,
            IScheduler buildThrottleScheduler = null)
        {
            Name = name ?? directory?.Name ?? throw new ArgumentException($"You must specify {nameof(name)}, {nameof(directory)}, or both.");
            _initializer = initializer ?? new PackageInitializer("console", Name);
            ConstructionTime = Clock.Current.Now();
            Directory = directory ?? new DirectoryInfo(Path.Combine(DefaultPackagesDirectory.FullName, Name));
            LastBuildErrorLogFile = new FileInfo(Path.Combine(Directory.FullName, ".trydotnet-builderror"));
            _log = new Logger($"{nameof(Package)}:{Name}");
            _buildThrottleScheduler = buildThrottleScheduler ?? TaskPoolScheduler.Default;

            _fullBuildRequestChannel = new Subject<Budget>();
            _fullBuildThrottlerSubscription = new SerialDisposable();

            _designTimeBuildRequestChannel = new Subject<Budget>();
            _designTimeBuildThrottlerSubscription = new SerialDisposable();

            SetupWorkspaceCreationFromBuildChannel();
            SetupWorkspaceCreationFromDesignTimeBuildChannel();
            TryLoadDesignTimeBuildFromBuildLog();
            _lazyCreation = new AsyncLazy<bool>(Create);
        }


        private void TryLoadDesignTimeBuildFromBuildLog()
        {
            if (Directory.Exists)
            {
                var binLog = Directory.GetFiles("*.binlog").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (binLog != null)
                {
                    var projectFile = Directory.GetFiles("*.csproj").FirstOrDefault();
                    if (projectFile != null && binLog.LastWriteTimeUtc >= projectFile.LastWriteTimeUtc)
                    {
                        var manager = new AnalyzerManager();
                        var result = manager.Analyze(binLog.FullName).FirstOrDefault(p => p.ProjectFilePath == projectFile.FullName);
                        if (result != null)
                        {
                            RoslynWorkspace = null;
                            DesignTimeBuildResult = result;
                            LastDesignTimeBuild = binLog.LastWriteTimeUtc;
                            if (result.Succeeded && !binLog.Name.EndsWith(DesignTimeBuildBinlogFileName))
                            {
                                LastSuccessfulBuildTime = binLog.LastWriteTimeUtc;
                            }
                        }
                    }
                }
            }
        }

        private DateTimeOffset? LastDesignTimeBuild { get; set; }

        private bool IsDirectoryCreated { get; set; }
        private FileInfo LastBuildErrorLogFile { get; }

        public DateTimeOffset? ConstructionTime { get; }

        public DateTimeOffset? CreationTime { get; private set; }

        public DateTimeOffset? LastSuccessfulBuildTime { get; private set; }

        public DateTimeOffset? PublicationTime { get; private set; }

        public bool IsUnitTestProject =>
            _isUnitTestProject ??
            (_isUnitTestProject = Directory.GetFiles("*.testadapter.dll", SearchOption.AllDirectories).Any()).Value;

        public bool IsWebProject
        {
            get
            {
                if (_isWebProject == null &&
                    Directory.GetFiles("*.csproj").SingleOrDefault() is FileInfo csproj)
                {
                    var csprojXml = File.ReadAllText(csproj.FullName);

                    var xml = XElement.Parse(csprojXml);

                    var wut = xml.XPathSelectElement("//ItemGroup/PackageReference[@Include='Microsoft.AspNetCore.App']");

                    _isWebProject = wut != null;
                }

                return _isWebProject ?? false;
            }
        }

        public DirectoryInfo Directory { get; }

        public string Name { get; }

        public static DirectoryInfo DefaultPackagesDirectory { get; }

        public FileInfo EntryPointAssemblyPath => _entryPointAssemblyPath ?? (_entryPointAssemblyPath = GetEntryPointAssemblyPath(Directory, IsWebProject));

        public string TargetFramework => _targetFramework ?? (_targetFramework = GetTargetFramework(Directory));

        public DateTimeOffset? ReadyTime { get; set; }

        protected async Task<bool> Create()
        {
            using (var operation = _log.OnEnterAndConfirmOnExit())
            {
                //to do: identify if this flag is needed
                if (!IsDirectoryCreated)
                {
                    Directory.Refresh();

                    if (!Directory.Exists)
                    {
                        operation.Info("Creating directory {directory}", Directory);
                        Directory.Create();
                        Directory.Refresh();
                    }

                    IsDirectoryCreated = true;
                }

                if (Directory.GetFiles().Length == 0)
                {
                    operation.Info("Initializing package using {_initializer} in {directory}", _initializer, Directory);
                    await _initializer.Initialize(Directory);
                }

                CreationTime = Clock.Current.Now();
                operation.Succeed();
                return true;
            }
        }

        public Task<Workspace> CreateRoslynWorkspaceForRunAsync(Budget budget)
        {
            var shouldBuild = ShouldDoFullBuild();
            if (!shouldBuild)
            {
                var ws = RoslynWorkspace ?? CreateRoslynWorkspace();
                if (ws != null)
                {
                    return Task.FromResult(ws);
                }
            }

            return RequestFullBuild(budget);
        }

        public Task<Workspace> CreateRoslynWorkspaceForLanguageServicesAsync(Budget budget)
        {
            var shouldBuild = ShouldDoDesignTimeFullBuild();
            if (!shouldBuild)
            {
                var ws = RoslynWorkspace ?? CreateRoslynWorkspace();
                if (ws != null)
                {
                    return Task.FromResult(ws);
                }
            }

            return RequestDesignTimeBuild(budget);
        }

        private void CreateCompletionSourceIfNeeded(ref TaskCompletionSource<Workspace> completionSource, object lockObject)
        {
            lock (lockObject)
            {
                switch (completionSource.Task.Status)
                {
                    case TaskStatus.Canceled:
                    case TaskStatus.Faulted:
                    case TaskStatus.RanToCompletion:
                        completionSource = new TaskCompletionSource<Workspace>();
                        break;
                }
            }
        }

        private void SetCompletionSourceResult(TaskCompletionSource<Workspace> completionSource, Workspace result, object lockObject)
        {
            lock (lockObject)
            {
                switch (completionSource.Task.Status)
                {
                    case TaskStatus.Canceled:
                    case TaskStatus.Faulted:
                    case TaskStatus.RanToCompletion:
                        return;
                    default:
                        completionSource.SetResult(result);
                        break;
                }
            }
        }

        private void SetCompletionSourceException(TaskCompletionSource<Workspace> completionSource, Exception exception, object lockObject)
        {
            lock (lockObject)
            {
                switch (completionSource.Task.Status)
                {
                    case TaskStatus.Canceled:
                    case TaskStatus.Faulted:
                    case TaskStatus.RanToCompletion:
                        return;
                    default:
                        completionSource.SetException(exception);
                        break;
                }
            }
        }

        private Task<Workspace> RequestFullBuild(Budget budget)
        {
            CreateCompletionSourceIfNeeded(ref _fullBuildCompletionSource, _fullBuildCompletionSourceLock);
            _fullBuildRequestChannel.OnNext(budget);
            return _fullBuildCompletionSource.Task;
        }

        private Task<Workspace> RequestDesignTimeBuild(Budget budget)
        {
            CreateCompletionSourceIfNeeded(ref _designTimeBuildCompletionSource, _designTimeBuildCompletionSourceLock);

            _designTimeBuildRequestChannel.OnNext(budget);
            return _designTimeBuildCompletionSource.Task;
        }

        private void SetupWorkspaceCreationFromBuildChannel()
        {
            _fullBuildThrottlerSubscription.Disposable = _fullBuildRequestChannel
                .Throttle(TimeSpan.FromSeconds(0.5), _buildThrottleScheduler)
                .Subscribe(
                      async (budget) =>
                      {
                          await ProcessFullBuildRequest(budget);
                      },
                  error =>
                  {
                      SetCompletionSourceException(_fullBuildCompletionSource, error, _fullBuildCompletionSourceLock);
                      SetupWorkspaceCreationFromBuildChannel();
                  });
        }

        private void SetupWorkspaceCreationFromDesignTimeBuildChannel()
        {
            _designTimeBuildThrottlerSubscription.Disposable = _designTimeBuildRequestChannel
                .Throttle(TimeSpan.FromSeconds(0.5), _buildThrottleScheduler)
                .Subscribe(
                    async (budget) =>
                    {
                        await ProcessDesignTimeBuildRequest(budget);
                    },
                    error =>
                    {
                        SetCompletionSourceException(_designTimeBuildCompletionSource, error, _designTimeBuildCompletionSourceLock);
                        SetupWorkspaceCreationFromDesignTimeBuildChannel();
                    });
        }

        private async Task ProcessFullBuildRequest(Budget budget)
        {
            await EnsureCreated().CancelIfExceeds(budget);
            await EnsureBuilt().CancelIfExceeds(budget);
            var ws = CreateRoslynWorkspace();
            if (IsWebProject)
            {
                await EnsurePublished().CancelIfExceeds(budget);
            }
            SetCompletionSourceResult(_fullBuildCompletionSource, ws, _fullBuildCompletionSourceLock);
        }

        private async Task ProcessDesignTimeBuildRequest(Budget budget)
        {
            await EnsureCreated().CancelIfExceeds(budget);
            await EnsureDesignTimeBuilt().CancelIfExceeds(budget);
            var ws = CreateRoslynWorkspace();
            SetCompletionSourceResult(_designTimeBuildCompletionSource, ws, _designTimeBuildCompletionSourceLock);
        }

        private Workspace CreateRoslynWorkspace()
        {
            var build = DesignTimeBuildResult;
            if (build == null)
            {
                return null;
            }

            var ws = build.GetWorkspace();
            var projectId = ws.CurrentSolution.ProjectIds.FirstOrDefault();
            var references = build.References;
            var metadataReferences = references.GetMetadataReferences();
            var solution = ws.CurrentSolution;
            solution = solution.WithProjectMetadataReferences(projectId, metadataReferences);
            ws.TryApplyChanges(solution);
            RoslynWorkspace = ws;
            return ws;
        }

        protected AdhocWorkspace RoslynWorkspace { get; set; }

        public async Task EnsureReady(Budget budget)
        {
            budget = budget ?? new Budget();

            await EnsureCreated().CancelIfExceeds(budget);

            await EnsureBuilt().CancelIfExceeds(budget);

            if (RequiresPublish)
            {
                await EnsurePublished().CancelIfExceeds(budget);
            }

            budget.RecordEntry();
        }

        protected Task<bool> EnsureCreated() => _lazyCreation.ValueAsync();

        protected async Task EnsureBuilt([CallerMemberName] string caller = null)
        {
            await EnsureCreated();
            using (var operation = _log.OnEnterAndConfirmOnExit())
            {
                if (ShouldDoFullBuild())
                {
                    await FullBuild();
                }
                else
                {
                    operation.Info("Workspace already built");
                }

                operation.Succeed();
            }
        }

        protected async Task EnsureDesignTimeBuilt([CallerMemberName] string caller = null)
        {
            await EnsureCreated();
            using (var operation = _log.OnEnterAndConfirmOnExit())
            {
                if (ShouldDoDesignTimeFullBuild())
                {
                    DesignTimeBuild();
                }
                else
                {
                    operation.Info("Workspace already built");
                }

                operation.Succeed();
            }
        }

        public virtual async Task EnsurePublished()
        {
            await EnsureBuilt();
            using (var operation = _log.OnEnterAndConfirmOnExit())
            {
                if (PublicationTime == null || PublicationTime < LastSuccessfulBuildTime)
                {
                    await Publish();
                }
                operation.Succeed();
            }
        }

        public bool RequiresPublish => IsWebProject;

        public async Task FullBuild()
        {
            using (var operation = _log.OnEnterAndConfirmOnExit())
            {
                try
                {
                    operation.Info("Attempting building package {name}", Name);

                    var buildInProgress = _buildSemaphore.CurrentCount == 0;
                    await _buildSemaphore.WaitAsync();
                    CommandLineResult result;
                    using (Disposable.Create(() => _buildSemaphore.Release()))
                    {
                        if (buildInProgress)
                        {
                            operation.Info("Skipping build for package {name}", Name);
                            return;
                        }

                        operation.Info("Building workspace using {_initializer} in {directory}", _initializer, Directory);
                        result = await new Dotnet(Directory)
                            .Build(args: "/bl");
                    }

                    TryLoadDesignTimeBuildFromBuildLog();
                    if (result.ExitCode != 0)
                    {
                        File.WriteAllText(
                            LastBuildErrorLogFile.FullName,
                            string.Join(Environment.NewLine, result.Error));
                    }
                    else if (LastBuildErrorLogFile.Exists)
                    {
                        LastBuildErrorLogFile.Delete();
                    }


                    result.ThrowOnFailure();
                    operation.Info("Workspace built");

                    operation.Succeed();
                }
                catch (Exception exception)
                {
                    operation.Error("Exception building workspace", exception);
                }

            }
        }

        protected async Task Publish()
        {
            using (var operation = _log.OnEnterAndConfirmOnExit())
            {
                operation.Info("Attempting to publish package {name}", Name);
                var publishInProgress = _publishSemaphore.CurrentCount == 0;
                await _publishSemaphore.WaitAsync();

                if (publishInProgress)
                {
                    operation.Info("Skipping publish for package {name}", Name);
                    return;
                }

                CommandLineResult result;
                using (Disposable.Create(() => _publishSemaphore.Release()))
                {
                    operation.Info("Publishing workspace in {directory}", Directory);
                    result = await new Dotnet(Directory)
                        .Publish("--no-dependencies --no-restore --no-build");
                }

                result.ThrowOnFailure();

                operation.Info("Workspace published");
                operation.Succeed();
                PublicationTime = Clock.Current.Now();
            }
        }

        public static async Task<Package> Copy(
            Package fromPackage,
            string folderNameStartsWith = null,
            bool isRebuildable = false,
            IScheduler buildThrottleScheduler = null)
        {
            if (fromPackage == null)
            {
                throw new ArgumentNullException(nameof(fromPackage));
            }

            await fromPackage.EnsureReady(new Budget());

            folderNameStartsWith = folderNameStartsWith ?? fromPackage.Name;
            var parentDirectory = fromPackage.Directory.Parent;

            var destination = CreateDirectory(folderNameStartsWith, parentDirectory);

            return await Copy(fromPackage, destination, isRebuildable, buildThrottleScheduler);
        }

        private static async Task<Package> Copy(Package fromPackage,
            DirectoryInfo destination,
            bool isRebuildable,
            IScheduler buildThrottleScheduler)
        {
            if (fromPackage == null)
            {
                throw new ArgumentNullException(nameof(fromPackage));
            }

            await fromPackage.EnsureReady(new Budget());

            fromPackage.Directory.CopyTo(destination);

            Package copy;
            if (isRebuildable)
            {
                copy = new RebuildablePackage(directory: destination, name: destination.Name, buildThrottleScheduler: buildThrottleScheduler)
                {
                    IsDirectoryCreated = true
                };
            }
            else
            {
                copy = new NonrebuildablePackage(directory: destination, name: destination.Name, buildThrottleScheduler: buildThrottleScheduler)
                {
                    IsDirectoryCreated = true
                };
            }

            Log.Info(
                "Copied workspace {from} to {to}",
                fromPackage,
                copy);

            return copy;
        }

        public static DirectoryInfo CreateDirectory(
            string folderNameStartsWith,
            DirectoryInfo parentDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(folderNameStartsWith))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(folderNameStartsWith));
            }

            parentDirectory = parentDirectory ?? DefaultPackagesDirectory;

            DirectoryInfo created;

            lock (_lockObj)
            {
                var existingFolders = parentDirectory.GetDirectories($"{folderNameStartsWith}.*");

                created = parentDirectory.CreateSubdirectory($"{folderNameStartsWith}.{existingFolders.Length + 1}");
            }

            return created;
        }

        public override string ToString()
        {
            return $"{Name} ({Directory.FullName}) ({new { CreationTime, LastSuccessfulBuildTime, PublicationTime }})";
        }

        protected SyntaxTree CreateInstrumentationEmitterSyntaxTree()
        {
            var resourceName = "WorkspaceServer.Servers.Roslyn.Instrumentation.InstrumentationEmitter.cs";

            var assembly = typeof(PackageExtensions).Assembly;

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException($"Resource \"{resourceName}\" not found"), Encoding.UTF8))
            {
                var source = reader.ReadToEnd();

                var parseOptions = DesignTimeBuildResult.GetCSharpParseOptions();
                var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(source), parseOptions);

                return syntaxTree;
            }
        }

        protected AnalyzerResult DesignTimeBuildResult { get; set; }

        protected virtual bool ShouldDoFullBuild()
        {
            return LastSuccessfulBuildTime == null
                   || ShouldDoDesignTimeFullBuild()
                   || (LastDesignTimeBuild > LastSuccessfulBuildTime);
        }

        protected virtual bool ShouldDoDesignTimeFullBuild()
        {
            return DesignTimeBuildResult == null
                   || (DesignTimeBuildResult.Succeeded == false);
        }

        protected AnalyzerResult DesignTimeBuild()
        {
            using (var operation = _log.OnEnterAndConfirmOnExit())
            {
                var csProj = Directory.GetFiles("*.csproj").FirstOrDefault();
                var logWriter = new StringWriter();
                var manager = new AnalyzerManager(new AnalyzerManagerOptions
                {
                    LogWriter = logWriter
                });

                var analyzer = manager.GetProject(csProj.FullName);
                analyzer.AddBinaryLogger(Path.Combine(Directory.FullName, DesignTimeBuildBinlogFileName));
                analyzer.SetGlobalProperty("langVersion", CSharpLanguageVersion);
                var result = analyzer.Build().Results.First();
                DesignTimeBuildResult = result;
                LastDesignTimeBuild = Clock.Current.Now();
                if (result.Succeeded == false)
                {
                    File.WriteAllText(
                        LastBuildErrorLogFile.FullName,
                        string.Join(Environment.NewLine, logWriter.ToString()));
                }
                else if (LastBuildErrorLogFile.Exists)
                {
                    LastBuildErrorLogFile.Delete();
                }

                operation.Succeed();

                return result;
            }
        }

        public virtual SyntaxTree GetInstrumentationEmitterSyntaxTree() =>
            CreateInstrumentationEmitterSyntaxTree();

        private static string GetTargetFramework(DirectoryInfo directory)
        {
            var runtimeConfig = directory.GetFiles("*.runtimeconfig.json", SearchOption.AllDirectories).FirstOrDefault();

            if (runtimeConfig != null)
            {
                return RuntimeConfig.GetTargetFramework(runtimeConfig);
            }

            return "netstandard2.0";
        }

        private static FileInfo GetEntryPointAssemblyPath(DirectoryInfo directory, bool isWebProject)
        {
            var depsFile = directory.GetFiles("*.deps.json", SearchOption.AllDirectories).FirstOrDefault();

            if (depsFile == null)
            {
                return null;
            }

            var entryPointAssemblyName = DepsFileParser.GetEntryPointAssemblyName(depsFile);

            var path =
                Path.Combine(
                    directory.FullName,
                    "bin",
                    "Debug",
                    GetTargetFramework(directory));

            if (isWebProject)
            {
                path = Path.Combine(path, "publish");
            }

            return new FileInfo(Path.Combine(path, entryPointAssemblyName));
        }
    }
}
