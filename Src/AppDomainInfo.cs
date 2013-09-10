﻿using System;
using RT.Util.ExtensionMethods;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using RT.PropellerApi;
using RT.Servers;
using RT.Util;

namespace RT.Propeller
{
    sealed class AppDomainInfo : IDisposable
    {
        public AppDomain AppDomain { get; private set; }
        public UrlMapping[] UrlMappings { get; private set; }
        public AppDomainRunner Runner { get; private set; }
        public PropellerModuleSettings ModuleSettings { get; private set; }
        public ISettingsSaver Saver { get; private set; }

        private int _filesChangedCount = 0;
        private string _fileChanged = null;
        private string _copyToPath;
        private LoggerBase _log;
        private List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();

        private static int _appDomainCount = 0;     // only used to give each AppDomain a unique name

        public AppDomainInfo(LoggerBase log, PropellerSettings settings, PropellerModuleSettings moduleSettings, ISettingsSaver saver)
        {
            ModuleSettings = moduleSettings;
            Saver = saver;
            _log = log;

            // Determine the temporary folder that DLLs will be copied to
            var tempFolder = settings.TempFolder ?? Path.GetTempPath();
            Directory.CreateDirectory(tempFolder);

            // Find a new folder to put the DLL/EXE files into
            int j = 1;
            do { _copyToPath = Path.Combine(tempFolder, "propeller-tmp-" + (j++)); }
            while (Directory.Exists(_copyToPath));
            Directory.CreateDirectory(_copyToPath);

            // Copy all the DLLs/EXEs to the temporary folder
            foreach (var sourceFile in
                new[] { typeof(PropellerEngine), typeof(IPropellerModule), typeof(HttpServer), typeof(Ut) }.Select(type => type.Assembly.Location).Concat(
                Directory.EnumerateFiles(Path.GetDirectoryName(moduleSettings.ModuleDll), "*.exe").Concat(
                Directory.EnumerateFiles(Path.GetDirectoryName(moduleSettings.ModuleDll), "*.dll"))))
            {
                var destFile = Path.Combine(_copyToPath, Path.GetFileName(sourceFile));
                if (File.Exists(destFile))
                    _log.Warn("Skipping file {0} because destination file {1} already exists.".Fmt(sourceFile, destFile));
                else
                {
                    _log.Info("Copying file {0} to {1}".Fmt(sourceFile, destFile));
                    File.Copy(sourceFile, destFile);
                }
            }

            // Create an AppDomain
            var setup = new AppDomainSetup { ApplicationBase = _copyToPath, PrivateBinPath = _copyToPath };
            AppDomain = AppDomain.CreateDomain("Propeller AppDomain #{0}, module {1}".Fmt(_appDomainCount++, moduleSettings.ModuleName), null, setup);
            Runner = (AppDomainRunner) AppDomain.CreateInstanceAndUnwrap("Propeller", "RT.Propeller.AppDomainRunner");
            Runner.Init(
                Path.Combine(_copyToPath, Path.GetFileName(moduleSettings.ModuleDll)),
                moduleSettings.ModuleType,
                moduleSettings.ModuleName,
                moduleSettings.Settings,
                _log,
                saver);

            var filters = Runner.FileFiltersToBeMonitoredForChanges;
            if (filters != null)
                foreach (var filter in filters)
                    addFileSystemWatcher(Path.GetDirectoryName(filter), Path.GetFileName(filter));

            UrlMappings = moduleSettings.Hooks.Select(hook => new UrlMapping(hook, Runner.Handle, true)).ToArray();

            _log.Info("Module {0} URLs: {1}".Fmt(moduleSettings.ModuleName, moduleSettings.Hooks.JoinString("; ")));
        }

        private void addFileSystemWatcher(string path, string filter)
        {
            var watcher = new FileSystemWatcher();
            watcher.Path = path;
            watcher.Filter = filter;
            watcher.Changed += fileSystemChangeDetected;
            watcher.Created += fileSystemChangeDetected;
            watcher.Deleted += fileSystemChangeDetected;
            watcher.Renamed += fileSystemChangeDetected;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        private void fileSystemChangeDetected(object sender, FileSystemEventArgs e)
        {
            _filesChangedCount++;
            _fileChanged = e.FullPath;
        }

        public bool MustReinitialize
        {
            get
            {
                if (_filesChangedCount > 0)
                {
                    _log.Info(@"Module {2}: Detected {0} changes to the filesystem, including ""{1}"".".Fmt(_filesChangedCount, _fileChanged, ModuleSettings.ModuleName));
                    _filesChangedCount = 0;
                    return true;
                }

                if (Runner.MustReinitialize)
                {
                    _log.Info(@"Module {0} asks to be reinitialized.".Fmt(ModuleSettings.ModuleName));
                    return true;
                }

                return false;
            }
        }

        public void Dispose()
        {
            foreach (var watcher in _watchers)
                watcher.Dispose();
            _watchers.Clear();
        }
    }
}
