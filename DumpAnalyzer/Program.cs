﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using CommandLine;
using Redmine.Net.Api;
using Redmine.Net.Api.Types;

namespace DumpAnalyzer
{
    internal class Program
    {
        private static string _user;
        private static string _password;
        private static RedmineManager _redmineManager;
        private static Project _project;
        private static List<IdentifiableName> _projectMembers;

        private static void Main(string[] args)
        {
            var options = new ProgramOptions();
            if (!Parser.Default.ParseArguments(args, options))
            {
                Environment.Exit(1);
            }
            if (!options.IsValid)
            {
                Console.Error.WriteLine("The options are invalid");
                Environment.Exit(1);
            }

            options.Normalize();

            Configuration configuration;
            if (!string.IsNullOrEmpty(options.ConfigFile))
            {
                // load config from file
                var xs = new XmlSerializer(typeof (Configuration));
                using (var fs = new FileStream(options.ConfigFile, FileMode.Open))
                {
                    configuration = (Configuration) xs.Deserialize(fs);
                }
            }
            else
            {
                configuration = Configuration.FromProgramOptions(options);
            }

            while (!GetCredentialsIfNeeded(configuration))
            {
                Console.WriteLine("The credentials you supplied were wrong...");
                Console.WriteLine();
            }

            if (!GetProjectDetailsIfNeeded(configuration))
            {
                Console.WriteLine("The project details you supplied were wrong...");
                Environment.Exit(1);
            }

            Process(configuration);
        }

        private static bool GetProjectDetailsIfNeeded(Configuration configuration)
        {
            if (configuration.OpenTickets)
            {
                try
                {
                    _project = _redmineManager.GetObject<Project>(configuration.Project, null);
                    _projectMembers =
                        _redmineManager.GetTotalObjectList<ProjectMembership>(new NameValueCollection
                            {
                                {"project_id", _project.Identifier}
                            }).Select(p => p.User).ToList();
                    return _projectMembers != null && _projectMembers.Any();
                }
                catch (RedmineException)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool GetCredentialsIfNeeded(Configuration configuration)
        {
            if (configuration.OpenTickets)
            {
                // Get use name and password
                Console.Write("Redmine Username: ");
                _user = Console.ReadLine();

                Console.Write("Redmine password: ");
                _password = Console.ReadLine();

                _redmineManager = new RedmineManager(configuration.RedmineUrl, _user, _password);
                try
                {
                    _redmineManager.GetCurrentUser();
                    return true;
                }
                catch (RedmineException)
                {
                    return false;
                }
            }

            return true;
        }

        private static void Process(Configuration configuration)
        {
            string[] dumps;
            if (!string.IsNullOrEmpty(configuration.DumpFile))
            {
                dumps = new[] {configuration.DumpFile};
            }
            else
            {
                SearchOption searchOptions = configuration.RecursiveSearch
                                                 ? SearchOption.AllDirectories
                                                 : SearchOption.TopDirectoryOnly;
                dumps = Directory.GetFiles(configuration.DumpsFolder, "*.dmp", searchOptions);
            }

            int counter = 1;
            foreach (string d in dumps)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("Analyzing {0}/{1}: {2}", counter++, dumps.Length, d);
                    Console.ResetColor();
                    Process(d, configuration);
                }
                catch (Exception e)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine("Error while analyzing {0}: {1}", d, e);
                    Console.ResetColor();
                }
                if (!configuration.OpenTickets)
                {
                    Console.WriteLine("Press <Enter> to continue...");
                    Console.ReadLine();
                }
            }
        }

        private static void Process(string dump, Configuration configuration)
        {
            Tuple<IList<StackFrame>, StackFrame, Filter> res = Analyze(dump, configuration);
            Report(res);
            OpenTicketIfNeeded(dump, res, configuration);
        }

        private static void OpenTicketIfNeeded(string dump, Tuple<IList<StackFrame>, StackFrame, Filter> res,
                                               Configuration configuration)
        {
            if (configuration.OpenTickets)
            {
                OpenTicket(dump, res, configuration);
            }
        }

        private static void Report(Tuple<IList<StackFrame>, StackFrame, Filter> res)
        {
            foreach (StackFrame stackFrame in res.Item1)
            {
                if (stackFrame == res.Item2)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                }
                Console.WriteLine(stackFrame);
                Console.ResetColor();
            }
        }

        private static void OpenTicket(string dump, Tuple<IList<StackFrame>, StackFrame, Filter> res,
                                       Configuration configuration)
        {
            OwnershipData ownershipData = configuration.Owners.FirstOrDefault(o => o.Filter == res.Item3);
            Owner assignee = configuration.DefaultOwner;
            if (ownershipData != null)
            {
                assignee = ownershipData.Owner;
            }

            var author = new IdentifiableName {Id = _redmineManager.GetCurrentUser().Id};
            IdentifiableName assignedTo =
                _projectMembers.SingleOrDefault(pm => pm != null && pm.Name == assignee.Name) ??
                _projectMembers.SingleOrDefault(pm => pm != null && pm.Name == configuration.DefaultOwner.Name);
            if (assignedTo == null)
            {
                // TODO: do something about this?
            }

            const string subject = "Investigate a dump";

            string description =
                string.Format(
                    "Please investigate a dump located at {0}.{1}{2}Here's the call stack for the last event:{3}{4}",
                    dump,
                    Environment.NewLine, Environment.NewLine, Environment.NewLine,
                    string.Join(Environment.NewLine, res.Item1));

            var issue = new Issue
                {
                    Subject = subject,
                    Description = description,
                    AssignedTo = assignedTo,
                    Author = author,
                    Project = new IdentifiableName {Id = _project.Id},
                };

            _redmineManager.CreateObject(issue);
        }

        private static Tuple<IList<StackFrame>, StackFrame, Filter> Analyze(string dump, Configuration configuration)
        {
            using (var da = new DumpAnalyzer(dump))
            {
                EventInformation lastEvent = da.GetLastEvent();
                IList<StackFrame> st = da.GetStackTrace(lastEvent.ThreadId);
                StackFrame frame = st.FirstOrDefault(f => configuration.Filters.Any(f.Match));
                Filter filter = frame == null ? null : configuration.Filters.First(frame.Match);
                return Tuple.Create(st, frame, filter);
            }
        }
    }
}