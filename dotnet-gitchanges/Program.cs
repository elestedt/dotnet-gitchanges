﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CommandLine;
using Gitchanges.Caches;
using Gitchanges.Changes;
using Gitchanges.Configuration;
using Gitchanges.Generators;
using Gitchanges.Readers;
using Gitchanges.Readers.Parsers;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Stubble.Core.Builders;

namespace Gitchanges
{
    class Program
    {
        private class Options
        {
            [Option('s', "settings", Required = false, HelpText = "Path to custom settings file.")]
            public string CustomSettingsPath { get; set; }
            [Option('t', "template", Required = false, HelpText = "Path to custom template file. Overrides value specified in custom settings file.")]
            public string CustomTemplatePath { get; set; }
            [Option('e', "exclude", Required = false, HelpText = "Comma separated tags to exclude. Overrides value specified in custom settings file.")]
            public string TagsToExclude { get; set; }
            [Option('m', "minVersion", Required = false, HelpText = "The minimum version of the changelog, will not include changes lower than this version. Overrides value specified in custom settings file.")]
            public string MinVersion { get; set; }
            [Option('r', "repository", Required = false, HelpText = "Path to repository root. Defaults to execution directory. Overrides value specified in custom settings file.")]
            public string RepositoryPath { get; set; }
            [Option('f', "fileSource", Required = false, HelpText = "Path to file source. Overrides value specified in custom settings file.")]
            public string FileSourcePath { get; set; }
        }
        
        static void Main(string[] args)
        {
            try
            {
                var configBuilder = new ConfigurationBuilder().AddJsonFile("appsettings.json");
                Parser.Default.ParseArguments<Options>(args)
                    .WithParsed(options =>
                    {
                        var additionalSettings = new List<KeyValuePair<string, string>>();

                        if (!string.IsNullOrEmpty(options.CustomSettingsPath)) 
                            configBuilder.AddJsonFile(options.CustomSettingsPath);

                        if (!string.IsNullOrEmpty(options.CustomTemplatePath))
                            additionalSettings.Add(new KeyValuePair<string, string>("Template", options.CustomTemplatePath));
                        
                        if (!string.IsNullOrEmpty(options.TagsToExclude))
                            additionalSettings.Add(new KeyValuePair<string, string>("TagsToExclude", options.TagsToExclude));
                        
                        if (!string.IsNullOrEmpty(options.MinVersion))
                            additionalSettings.Add(new KeyValuePair<string, string>("MinVersion", options.MinVersion));
                        
                        if (!string.IsNullOrEmpty(options.RepositoryPath))
                            additionalSettings.Add(new KeyValuePair<string, string>("Repository:Path", options.RepositoryPath));
                        
                        if (!string.IsNullOrEmpty(options.FileSourcePath))
                            additionalSettings.Add(new KeyValuePair<string, string>("FileSource", options.FileSourcePath));

                        configBuilder.AddInMemoryCollection(additionalSettings);
                    });
                
                var config = TryOrExit(() => configBuilder.Build(), "Failed to build configuration");
                var appConfig = config.Get<AppConfig>();
                
                var template = GetTemplate(appConfig.Template);
                var repo = TryOrExit(() => new Repository(appConfig.Repository.Path), "Failed to initialize repository");
                var idToOverrideChange = new Dictionary<string, IChange>();
                var tagsToExclude = appConfig.TagsToExclude.Split(",");
                var renderer = new StubbleBuilder().Build();

                if (appConfig.MultiProject)
                {
                    var readers = new List<IGenericReader<ProjectChange>>();
                    var idToProjectChange = new Dictionary<string, ProjectChange>();
                    if (!string.IsNullOrEmpty(appConfig.FileSource))
                    {
                        var fileReader = new FileReader<ProjectChange>(appConfig.FileSource, new ProjectFileSourceRowParser(Console.Error));
                        readers.Add(fileReader);
                    }
                    if (!string.IsNullOrEmpty(appConfig.Repository.OverrideSource))
                    {
                        var overrideFileReader = new FileReader<OverrideProjectChange>(appConfig.Repository.OverrideSource, new OverrideProjectSourceRowParser(Console.Error));
                        readers.Add(overrideFileReader);
                        idToProjectChange = overrideFileReader.Values().ToDictionary<OverrideProjectChange, string, ProjectChange>(change => change.Id, change => change);
                    }
                    var parser = new ProjectCommitParser(appConfig.Parsing);
                    var gitReader = new GitReader<ProjectChange>(repo, parser, idToProjectChange);
                    readers.Add(gitReader);
                    
                    var generator = new ProjectChangelogGenerator(readers, template, renderer);
                    var projectToOutput = generator.Generate(appConfig.MinVersion, tagsToExclude);

                    foreach (var (project, output) in projectToOutput)
                    {
                        File.WriteAllText($@"{project}-changelog.md", output);
                    }
                }
                else
                {
                    var readers = new List<IGenericReader<IChange>>();
                    
                    if (!string.IsNullOrEmpty(appConfig.FileSource))
                    {
                        var fileReader = new FileReader<DefaultChange>(appConfig.FileSource, new DefaultFileSourceRowParser(Console.Error));
                        readers.Add(fileReader);
                    }
                    
                    if (!string.IsNullOrEmpty(appConfig.Repository.OverrideSource))
                    {
                        var overrideFileReader = new FileReader<OverrideChange>(appConfig.Repository.OverrideSource, new OverrideSourceRowParser(Console.Error));
                        idToOverrideChange = overrideFileReader.Values().ToDictionary<OverrideChange, string, IChange>(change => change.Id, change => change);
                    }
                    
                    var gitReader = new GitReader<IChange>(repo, new DefaultCommitParser(appConfig.Parsing), idToOverrideChange);
                    readers.Add(gitReader);
                    
                    var cache = new ChangeCache();
                    var generator = new StringChangelogGenerator(readers, cache, template, renderer);
                    var output = generator.Generate(appConfig.MinVersion, tagsToExclude);
                    File.WriteAllText(@"changelog.md", output);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.Message}");
                Environment.Exit(-1);
            }
        }

        private static string GetTemplate(string templatePath)
        {
            return TryOrExit(() =>
            {
                var stream = string.IsNullOrEmpty(templatePath) ? Assembly.GetEntryAssembly()?.GetManifestResourceStream("Gitchanges.KeepAChangelogTemplate.Mustache") : File.OpenRead(templatePath);
                using (var streamReader = new StreamReader(stream, Encoding.UTF8))
                {
                    return streamReader.ReadToEnd();
                }
            }, "Failed to read template file");
        }

        private static T TryOrExit<T>(Func<T> action, string failureMessage)
        {
            try
            {
                return action.Invoke();
            }
            catch (Exception e)
            {
                Console.WriteLine($"{failureMessage}: {e.Message}");
                Environment.Exit(-1);
            }

            return default;
        }
    }
}
