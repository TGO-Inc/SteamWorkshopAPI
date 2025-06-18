using System.Collections.Concurrent;
using System.Text;

namespace SteamWorkshop.WebAPI.Managers;

public class TimestampedErrorWriter(TextWriter originalError) : TextWriter
{
    private readonly TextWriter _originalError = originalError;

    public override Encoding Encoding => this._originalError.Encoding;

    public override void Write(string? value)
        => this._originalError.Write($"{DateTime.Now}: {value ?? "null"}");

    public override void WriteLine(string? value)
        => this._originalError.WriteLine($"{DateTime.Now}: {value ?? "null"}");
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

    private sealed class ColorMessage : IColorMessage
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

    private sealed class ErrorMessage : IColorMessage
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

    private readonly ConcurrentQueue<object?> _messageQueue = new();
    private readonly Task _loggingTask;
    private readonly CancellationToken _token;
    private bool _isWaitingForExit = false;

    public readonly IColorMessage Colored;
    public readonly IColorMessage Error;

    public ConsoleManager(CancellationToken tok)
    {
        this._token = tok;

        TextWriter originalError = Console.Error;
        Console.SetError(new TimestampedErrorWriter(originalError));

        this._loggingTask = new Task(this.DoConsoleLog, this._token, TaskCreationOptions.LongRunning);

        this.Colored = new ColorMessage();
        this.Colored.OnWrite += this.ColoredWrite;

        this.Error = new ErrorMessage();
        this.Error.OnWrite += this.ColoredWrite;
    }

    private void ColoredWrite(IColorMessage message)
        => this._messageQueue.Enqueue(message);

    public void WriteLine(object? message)
        => this._messageQueue.Enqueue(message);

    public void WriteLine(params object[] message)
    {
        foreach (object obj in message)
            this._messageQueue.Enqueue(obj);
    }

    private async void DoConsoleLog()
    {
        try
        {
            while (!(this._token.IsCancellationRequested && this._isWaitingForExit))
            while (this._messageQueue.TryDequeue(out object? msg))
            {
                string fMsg;
                switch (msg)
                {
                    case IColorMessage cMsg:
                        if (cMsg.Color.HasValue)
                            Console.ForegroundColor = cMsg.Color.Value;
                        fMsg = $"[{DateTime.Now}] {cMsg.Message ?? "null"}";
                        Console.WriteLine(fMsg);
                        await File.AppendAllTextAsync("log.txt", fMsg+Environment.NewLine, this._token);
                        Console.ResetColor();
                        break;
                    default:
                        fMsg = $"[{DateTime.Now}] {msg ?? "null"}";
                        Console.WriteLine(fMsg);
                        await File.AppendAllTextAsync("log.txt", fMsg + Environment.NewLine, this._token);
                        break;
                }
                await Task.Delay(50, this._token);
            }
        }
        catch (Exception e)
        {
            throw; // TODO handle exception
        }
    }

    public void StartOutput()
    {
        // if (!this.LoggingTask.IsCompleted && !this.LoggingTask.IsCanceled)
        this._loggingTask.Start();
    }

    public void WaitForExit()
    {
        this._isWaitingForExit = true;
        this._loggingTask.Wait(this._token);
    }
}