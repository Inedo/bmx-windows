﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Inedo.BuildMaster.Extensibility.Operations;
using Inedo.Diagnostics;
using Inedo.ExecutionEngine;

namespace Inedo.BuildMasterExtensions.Windows.PowerShell
{
    internal class PowerShellScriptRunner : ILogger, IDisposable
    {
        private BuildMasterPSHost pshost = new BuildMasterPSHost();
        private Lazy<Runspace> runspaceFactory;
        private bool disposed;

        public PowerShellScriptRunner()
        {
            this.pshost.MessageLogged += (s, e) => this.Log(e.Level, e.Message);
            this.runspaceFactory = new Lazy<Runspace>(this.InitializeRunspace);
        }

        public event EventHandler<PowerShellOutputEventArgs> OutputReceived;
        public event EventHandler<LogMessageEventArgs> MessageLogged;

        public Runspace Runspace => this.runspaceFactory.Value;
        public bool DebugLogging { get; set; }
        public bool VerboseLogging { get; set; }

        private Runspace InitializeRunspace()
        {
            var runspace = RunspaceFactory.CreateRunspace(this.pshost);
            runspace.Open();
            return runspace;
        }

        public static Dictionary<string, string> ExtractVariables(string script, IOperationExecutionContext context)
        {
            var vars = ExtractVariablesInternal(script);
            var results = new Dictionary<string, string>();
            foreach (var var in vars)
            {
                if (RuntimeVariableName.IsLegalVariableName(var))
                {
                    var varName = new RuntimeVariableName(var, RuntimeValueType.Scalar);
                    var varValue = context.TryGetVariableValue(varName.ToString());
                    if (varValue != null)
                        results[var] = varValue.Value.AsString();
                }
            }

            return results;
        }

        public Task<int?> RunAsync(string script, CancellationToken cancellationToken)
        {
            return this.RunAsync(script, new Dictionary<string, string>(), new Dictionary<string, string>(), cancellationToken);
        }
        public Task<int?> RunAsync(string script, Dictionary<string, string> variables, Dictionary<string, string> outVariables, CancellationToken cancellationToken)
        {
            return this.RunAsync(script, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(), cancellationToken);
        }
        public async Task<int?> RunAsync(string script, Dictionary<string, string> variables, Dictionary<string, string> parameters, Dictionary<string, string> outVariables, CancellationToken cancellationToken)
        {
            var runspace = this.Runspace;

            var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.Runspace = runspace;

            foreach (var var in variables)
            {
                this.LogDebug($"Importing {var.Key}={var.Value}");
                runspace.SessionStateProxy.SetVariable(var.Key, var.Value);
            }

            if (this.DebugLogging)
                runspace.SessionStateProxy.SetVariable("DebugPreference", "Continue");

            if (this.VerboseLogging)
                runspace.SessionStateProxy.SetVariable("VerbosePreference", "Continue");

            var output = new PSDataCollection<PSObject>();
            output.DataAdded +=
                (s, e) =>
                {
                    var rubbish = output[e.Index];
                    this.OnOutputReceived(rubbish);
                };

            powerShell.Streams.AttachLogging(this);
            powerShell.AddScript(script);

            foreach (var p in parameters)
            {
                this.LogDebug($"Assigning parameter {p.Key}={p.Value}");
                powerShell.AddParameter(p.Key, p.Value);
            }

            int? exitCode = null;
            EventHandler<ShouldExitEventArgs> handleShouldExit = (s, e) => exitCode = e.ExitCode;
            this.pshost.ShouldExit += handleShouldExit;
            using (var registration = cancellationToken.Register(powerShell.Stop))
            {
                try
                {
                    await Task.Factory.FromAsync(powerShell.BeginInvoke((PSDataCollection<PSObject>)null, output), powerShell.EndInvoke);

                    foreach (var var in outVariables.Keys.ToList())
                        outVariables[var] = powerShell.Runspace.SessionStateProxy.GetVariable(var)?.ToString();
                }
                finally
                {
                    this.pshost.ShouldExit -= handleShouldExit;
                }
            }

            return exitCode;
        }

        public void Dispose()
        {
            if (!this.disposed && this.runspaceFactory.IsValueCreated)
            {
                this.runspaceFactory.Value.Close();
                this.runspaceFactory.Value.Dispose();
                this.disposed = true;
            }
        }

        private static IEnumerable<string> ExtractVariablesInternal(string script)
        {
            var variableRegex = new Regex(@"(?>\$(?<1>[a-zA-Z0-9_]+)|\${(?<2>[^}]+)})", RegexOptions.ExplicitCapture);

            Collection<PSParseError> errors;
            var tokens = PSParser.Tokenize(script, out errors);
            if (tokens == null)
                return Enumerable.Empty<string>();

            var vars = tokens
                .Where(v => v.Type == PSTokenType.Variable)
                .Select(v => v.Content)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var strings = tokens
                .Where(t => t.Type == PSTokenType.String)
                .Select(t => t.Content);

            foreach (var s in strings)
            {
                var matches = variableRegex.Matches(s);
                if (matches.Count > 0)
                {
                    foreach (Match match in matches)
                    {
                        if (match.Groups[1].Success)
                            vars.Add(match.Groups[1].Value);
                        else if (match.Groups[2].Success)
                            vars.Add(match.Groups[2].Value);
                    }
                }
            }

            return vars;
        }

        public void Log(MessageLevel logLevel, string message)
        {
            var handler = this.MessageLogged;
            if (handler != null)
                handler(this, new LogMessageEventArgs(logLevel, message));
        }

        private void OnOutputReceived(PSObject obj)
        {
            var handler = this.OutputReceived;
            if (handler != null)
                handler(this, new PowerShellOutputEventArgs(obj));
        }
    }
}
