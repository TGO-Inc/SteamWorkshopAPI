using System.Collections.Concurrent;

namespace SteamWorkshop.WebAPI.Managers
{
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
            this.LoggingTask = new Task(DoConsoleLog, Token, TaskCreationOptions.LongRunning);

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
                switch (msg)
                {
                    case IColorMessage cmsg:
                        if (cmsg.Color.HasValue) Console.ForegroundColor = cmsg.Color.Value;
                        Console.WriteLine(cmsg.Message);
                        Console.ResetColor();
                        break;
                    default:
                        Console.WriteLine(msg);
                        break;
                }
                await Task.Delay(50);
            }
        }

        public void StartOutput()
        {
            this.LoggingTask.Start();
        }

        public void WaitForExit()
        {
            this.IsWaitingForExit = true;
            this.LoggingTask.Wait();
        }
    }
}
