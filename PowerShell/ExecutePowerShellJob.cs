﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inedo.BuildMaster.Extensibility.Agents;
using Inedo.Diagnostics;
using Inedo.Serialization;

namespace Inedo.BuildMasterExtensions.Windows.PowerShell
{
    internal sealed class ExecutePowerShellJob : RemoteJob
    {
        public string ScriptText { get; set; }
        public bool DebugLogging { get; set; }
        public bool VerboseLogging { get; set; }
        public bool CollectOutput { get; set; }
        public bool LogOutput { get; set; }
        public Dictionary<string, string> Variables { get; set; }
        public Dictionary<string, string> Parameters { get; set; }
        public string[] OutVariables { get; set; }

        public override void Serialize(Stream stream)
        {
            var writer = new BinaryWriter(stream, InedoLib.UTF8Encoding);
            writer.Write(this.ScriptText ?? string.Empty);
            writer.Write(this.DebugLogging);
            writer.Write(this.VerboseLogging);
            writer.Write(this.CollectOutput);
            writer.Write(this.LogOutput);

            SlimBinaryFormatter.WriteLength(stream, this.Variables?.Count ?? 0);
            foreach (var var in this.Variables ?? new Dictionary<string, string>())
            {
                writer.Write(var.Key);
                writer.Write(var.Value ?? string.Empty);
            }

            SlimBinaryFormatter.WriteLength(stream, this.Parameters?.Count ?? 0);
            foreach (var var in this.Parameters ?? new Dictionary<string, string>())
            {
                writer.Write(var.Key);
                writer.Write(var.Value ?? string.Empty);
            }

            SlimBinaryFormatter.WriteLength(stream, this.OutVariables?.Length ?? 0);
            foreach (var var in this.OutVariables ?? new string[0])
                writer.Write(var);
        }
        public override void Deserialize(Stream stream)
        {
            var reader = new BinaryReader(stream, InedoLib.UTF8Encoding);
            this.ScriptText = reader.ReadString();
            this.DebugLogging = reader.ReadBoolean();
            this.VerboseLogging = reader.ReadBoolean();
            this.CollectOutput = reader.ReadBoolean();
            this.LogOutput = reader.ReadBoolean();

            int count = SlimBinaryFormatter.ReadLength(stream);
            this.Variables = new Dictionary<string, string>(count);
            for (int i = 0; i < count; i++)
            {
                var key = reader.ReadString();
                var value = reader.ReadString();
                this.Variables[key] = value;
            }

            count = SlimBinaryFormatter.ReadLength(stream);
            this.Parameters = new Dictionary<string, string>(count);
            for (int i = 0; i < count; i++)
            {
                var key = reader.ReadString();
                var value = reader.ReadString();
                this.Parameters[key] = value;
            }

            count = SlimBinaryFormatter.ReadLength(stream);
            this.OutVariables = new string[count];
            for (int i = 0; i < count; i++)
                this.OutVariables[i] = reader.ReadString();
        }

        public override async Task<object> ExecuteAsync(CancellationToken cancellationToken)
        {
            using (var runner = new PowerShellScriptRunner { DebugLogging = this.DebugLogging, VerboseLogging = this.VerboseLogging })
            {
                var outputData = new List<string>();

                runner.MessageLogged += (s, e) => this.Log(e.Level, e.Message);
                if (this.LogOutput)
                    runner.OutputReceived += (s, e) => this.LogInformation(e.Output?.ToString());

                if (this.CollectOutput)
                {
                    runner.OutputReceived +=
                        (s, e) =>
                        {
                            var output = e.Output?.ToString();
                            if (!string.IsNullOrWhiteSpace(output))
                            {
                                lock (outputData)
                                {
                                    outputData.Add(output);
                                }
                            }
                        };
                }

                var outVariables = this.OutVariables.ToDictionary(v => v, v => (string)null, StringComparer.OrdinalIgnoreCase);

                int? exitCode = await runner.RunAsync(this.ScriptText, this.Variables, this.Parameters, outVariables, cancellationToken);

                return new Result
                {
                    ExitCode = exitCode,
                    Output = outputData,
                    OutVariables = outVariables
                };
            }
        }

        public override void SerializeResponse(Stream stream, object result)
        {
            var data = (Result)result;
            var writer = new BinaryWriter(stream, InedoLib.UTF8Encoding);

            if (data.ExitCode == null)
            {
                stream.WriteByte(0);
            }
            else
            {
                writer.Write((byte)1);
                writer.Write(data.ExitCode.Value);
            }

            SlimBinaryFormatter.WriteLength(stream, data.Output.Count);
            foreach (var s in data.Output)
                writer.Write(s ?? string.Empty);

            SlimBinaryFormatter.WriteLength(stream, data.OutVariables.Count);
            foreach (var v in data.OutVariables)
            {
                writer.Write(v.Key ?? string.Empty);
                writer.Write(v.Value ?? string.Empty);
            }
        }
        public override object DeserializeResponse(Stream stream)
        {
            var reader = new BinaryReader(stream, InedoLib.UTF8Encoding);

            int? exitCode;
            if (stream.ReadByte() == 0)
                exitCode = null;
            else
                exitCode = reader.ReadInt32();

            int count = SlimBinaryFormatter.ReadLength(stream);
            var output = new List<string>(count);
            for (int i = 0; i < count; i++)
                output.Add(reader.ReadString());

            count = SlimBinaryFormatter.ReadLength(stream);
            var vars = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                var key = reader.ReadString();
                var value = reader.ReadString();
                vars[key] = value;
            }

            return new Result
            {
                ExitCode = exitCode,
                Output = output,
                OutVariables = vars
            };
        }

        public sealed class Result
        {
            public int? ExitCode { get; set; }
            public List<string> Output { get; set; }
            public Dictionary<string, string> OutVariables { get; set; }
        }
    }
}
