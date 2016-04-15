﻿using System.ComponentModel;
using System.Threading.Tasks;
using Inedo.BuildMaster.Extensibility;
using Inedo.BuildMaster.Extensibility.Agents;
using Inedo.BuildMaster.Extensibility.Operations;
using Inedo.BuildMasterExtensions.Windows.PowerShell;
using Inedo.Diagnostics;
using Inedo.Documentation;

namespace Inedo.BuildMasterExtensions.Windows.Operations
{
    [DisplayName("Execute PowerShell Script")]
    [Description("Executes a specified PowerShell script.")]
    [ScriptAlias("Execute-PowerShell")]
    [ScriptAlias("PSExec")]
    [ScriptNamespace("PowerShell", PreferUnqualified = true)]
    [DefaultProperty(nameof(ScriptText))]
    [Tag("powershell")]
    [Note("If you are attempting to write the results of a Format-* call to the log, you may see "
        + "messages similar to \"Microsoft.PowerShell.Commands.Internal.Format.FormatEntryData\". To convert this to text, "
        + "use the Out-String commandlet at the end of your command chain.")]
    [Note("This script will execute in simulation mode; you set the RunOnSimulation parameter to false to prevent this behavior, or you can use the $IsSimulation variable function within the script.")]
    [Example(@"
# writes the list of services running on the computer to the Otter log
psexec >>
    Get-Service | Where-Object {$_.Status -eq ""Running""} | Format-Table Name, DisplayName | Out-String
>>;

# delete all but the latest 3 logs in the log directory, and log any debug/verbose messages to the Otter log
psexec >>
    Get-ChildItem ""E:\Site\Logs"" | Sort-Object $.CreatedDate -descending | Select-Object -skip 3 | Remove-Item
>> (Verbose: true, Debug: true, RunOnSimulation: false);
")]
    public sealed class PSExecuteOperation : ExecuteOperation
    {
        [Required]
        [ScriptAlias("Text")]
        [Description("The PowerShell script text.")]
        [DisableVariableExpansion]
        public string ScriptText { get; set; }

        [ScriptAlias("Debug")]
        [Description("Captures the PowerShell Write-Debug stream into the Otter debug log. The default is false.")]
        public bool DebugLogging { get; set; }

        [ScriptAlias("Verbose")]
        [Description("Captures the PowerShell Write-Verbose stream into the Otter debug log. The default is false.")]
        public bool VerboseLogging { get; set; }

        [ScriptAlias("RunOnSimulation")]
        [DisplayName("Run on simulation")]
        [Description("Indicates whether the script will execute in simulation mode. The default is false.")]
        public bool RunOnSimulation { get; set; }

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            if (context.Simulation && !this.RunOnSimulation)
            {
                this.LogInformation("Executing PowerShell script...");
                return;
            }

            var jobRunner = context.Agent.GetService<IRemoteJobExecuter>();

            var job = new ExecutePowerShellJob
            {
                ScriptText = this.ScriptText,
                DebugLogging = this.DebugLogging,
                VerboseLogging = this.VerboseLogging,
                CollectOutput = false,
                LogOutput = true,
                Variables = PowerShellScriptRunner.ExtractVariables(this.ScriptText, context)
            };

            job.MessageLogged += (s, e) => this.Log(e.Level, e.Message);

            var result = (ExecutePowerShellJob.Result)await jobRunner.ExecuteJobAsync(job, context.CancellationToken);
            if (result.ExitCode != null)
                this.LogDebug("Script exit code: " + result.ExitCode);
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            return new ExtendedRichDescription(
                new RichDescription(
                    "Execute ",
                    new Hilite(config[nameof(this.ScriptText)])
                ),
                new RichDescription(
                    "using Windows PowerShell"
                )
            );
        }
    }
}