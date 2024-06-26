﻿using System.Collections.Concurrent;
using System.Text;

namespace SteamWorkshop.WebAPI.Managers
{
    public class TimestampedErrorWriter(TextWriter originalError) : TextWriter
    {
        private readonly TextWriter originalError = originalError;

        public override Encoding Encoding => originalError.Encoding;

        public override void Write(string? value)
        {
            originalError.Write($"{DateTime.Now}: {value ?? "null"}");
        }

        public override void WriteLine(string? value)
        {
            originalError.WriteLine($"{DateTime.Now}: {value ?? "null"}");
        }
    }

    public class ConsoleManager
    {
        public delegate void WriteColored(IColorMessage message);
        
        public interface IColorMessage
        {
            public event WriteColored? OnWrite;
            public void ForegroundColor(ConsoleColor color);
            public ConsoleColor? Color { get; }
            public object? Message { get; }

            public void WriteLine(object? message);
        }

        internal sealed class ColorMessage : IColorMessage
        {
            public ConsoleColor? Color { get; private set; }

            public object? Message { get; private set; }

            public event WriteColored? OnWrite;

            public void ForegroundColor(ConsoleColor color) => Color = color;

            public void WriteLine(object? message)
            {
                this.Message = message;
                this.OnWrite!.Invoke(this);

                this.Message = null;
                this.Color = null;
            }
        }

        internal sealed class ErrorMessage : IColorMessage
        {
            public ConsoleColor? Color => ConsoleColor.Red;

            public object? Message { get; private set; }

            public event WriteColored? OnWrite;

            public void ForegroundColor(ConsoleColor color) { }

            public void WriteLine(object? message)
            {
                this.Message = message;
                this.OnWrite!.Invoke(this);

                this.Message = null;
            }
        }

        private readonly ConcurrentQueue<object?> MessageQueue = new();
        private readonly Task LoggingTask;
        private readonly CancellationToken Token;
        private bool IsWaitingForExit = false;

        public readonly IColorMessage Colored;
        public readonly IColorMessage Error;

        public ConsoleManager(CancellationToken tok)
        {
            this.Token = tok;

            TextWriter originalError = Console.Error;
            Console.SetError(new TimestampedErrorWriter(originalError));

            this.LoggingTask = new Task(DoConsoleLog, this.Token, TaskCreationOptions.LongRunning);

            this.Colored = new ColorMessage();
            this.Colored.OnWrite += ColoredWrite;

            this.Error = new ErrorMessage();
            this.Error.OnWrite += ColoredWrite;
        }

        private void ColoredWrite(IColorMessage message)
        {
            this.MessageQueue.Enqueue(message);
        }

        public void WriteLine(object? message)
        {
            this.MessageQueue.Enqueue(message);
        }

        public void WriteLine(params object[] message)
        {
            foreach (var obj in message)
                this.MessageQueue.Enqueue(obj);
        }

        private async void DoConsoleLog()
        {
            while (!(this.Token.IsCancellationRequested && this.IsWaitingForExit))
            while (this.MessageQueue.TryDequeue(out var msg))
            {
                string fmsg;
                switch (msg)
                {
                    case IColorMessage cmsg:
                        if (cmsg.Color is not null && cmsg.Color.HasValue)
                            Console.ForegroundColor = cmsg.Color.Value;
                        fmsg = $"[{DateTime.Now}] {cmsg.Message ?? "null"}";
                        Console.WriteLine(fmsg);
                        File.AppendAllText("log.txt", fmsg+Environment.NewLine);
                        Console.ResetColor();
                        break;
                    default:
                        fmsg = $"[{DateTime.Now}] {msg ?? "null"}";
                        Console.WriteLine(fmsg);
                        File.AppendAllText("log.txt", fmsg + Environment.NewLine);
                        break;
                }
                await Task.Delay(50);
            }
        }

        public void StartOutput()
        {
            // if (!this.LoggingTask.IsCompleted && !this.LoggingTask.IsCanceled)
            this.LoggingTask.Start();
        }

        public void WaitForExit()
        {
            this.IsWaitingForExit = true;
            this.LoggingTask.Wait();
        }
    }
}
