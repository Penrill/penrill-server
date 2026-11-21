using System.Text;
using System.Text.RegularExpressions;

namespace Penrill.Server.Cli;

public sealed record SelectOption<T>(string Label, T Value, string? Detail = null);

/// <summary>
/// Interactive terminal widgets (rounded status boxes, arrow-key select menus, validated inputs and
/// masked password prompts) using standard ANSI escape sequences with zero external dependencies.
/// </summary>
public static partial class TerminalUi
{
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";
    public const string Cyan = "\x1b[38;5;45m";
    public const string Green = "\x1b[38;5;42m";
    public const string Amber = "\x1b[38;5;214m";
    public const string Red = "\x1b[38;5;203m";
    public const string Muted = "\x1b[38;5;245m";

    private static bool cancelHandlerRegistered;

    public static bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static void EnsureCursorRestoredOnExit()
    {
        if (cancelHandlerRegistered) return;
        cancelHandlerRegistered = true;
        Console.CancelKeyPress += (_, _) => Console.Out.Write("\x1b[?25h" + Reset + "\n");
    }

    public static void Success(string message) => Console.WriteLine($"  {Green}✓{Reset} {message}");

    public static void Warn(string message) => Console.WriteLine($"  {Amber}!{Reset} {message}");

    public static void Error(string message) => Console.WriteLine($"  {Red}✗{Reset} {message}");

    public static void Info(string message) => Console.WriteLine($"  {Cyan}ℹ{Reset} {message}");

    public static void Header(string title, string? subtitle = null)
    {
        Console.WriteLine();
        Console.WriteLine($"  {Bold}{Cyan}◆ {title}{Reset}" + (subtitle is not null ? $"  {Muted}{subtitle}{Reset}" : ""));
        Console.WriteLine($"  {Muted}{new string('─', 62)}{Reset}");
    }

    public static void DrawBox(string title, string badge, IReadOnlyList<(string Key, string Value)> rows)
    {
        const int innerWidth = 64;
        Console.WriteLine();
        Console.WriteLine($"  {Muted}╭{new string('─', innerWidth)}╮{Reset}");

        var titlePlain = $"  ● {title}";
        var badgePlain = $"{badge}  ";
        var gap = Math.Max(1, innerWidth - VisibleLength(titlePlain) - VisibleLength(badgePlain));
        Console.WriteLine($"  {Muted}│{Reset}  {Green}●{Reset} {Bold}{title}{Reset}{new string(' ', gap)}{Cyan}{badge}{Reset}  {Muted}│{Reset}");
        Console.WriteLine($"  {Muted}├{new string('─', innerWidth)}┤{Reset}");

        foreach (var (key, value) in rows)
        {
            var keyCell = $"  {key,-13} ";
            var maxVal = innerWidth - keyCell.Length - 2;
            var valText = TruncateVisible(value, maxVal);
            var pad = Math.Max(0, innerWidth - keyCell.Length - VisibleLength(valText));
            Console.WriteLine($"  {Muted}│{Reset}{Muted}{keyCell}{Reset}{valText}{new string(' ', pad)}{Muted}│{Reset}");
        }

        Console.WriteLine($"  {Muted}╰{new string('─', innerWidth)}╯{Reset}");
        Console.WriteLine();
    }

    public static (bool Selected, T Value) Select<T>(string title, IReadOnlyList<SelectOption<T>> options, int initialIndex = 0, string? hint = null)
    {
        if (options.Count == 0) return (false, default!);
        if (!IsInteractive)
            return (true, options[Math.Clamp(initialIndex, 0, options.Count - 1)].Value);

        EnsureCursorRestoredOnExit();
        var index = Math.Clamp(initialIndex, 0, options.Count - 1);
        var maxLabel = options.Max(o => VisibleLength(o.Label));
        var hintText = hint ?? "↑/↓ move · Enter select · Esc back";

        Console.Out.Write("\x1b[?25l");
        var renderedLines = 0;

        void Render()
        {
            if (renderedLines > 0)
            {
                for (var i = 0; i < renderedLines; i++)
                    Console.Out.Write("\x1b[1A\r\x1b[2K");
            }
            var lines = new List<string>
            {
                $"  {Cyan}?{Reset} {Bold}{title}{Reset}  {Muted}({hintText}){Reset}"
            };
            for (var i = 0; i < options.Count; i++)
            {
                var opt = options[i];
                var active = i == index;
                var pad = new string(' ', Math.Max(2, maxLabel - VisibleLength(opt.Label) + 3));
                var detail = opt.Detail is { Length: > 0 } d ? $"{pad}{Muted}{d}{Reset}" : "";
                lines.Add(active
                    ? $"  {Cyan}❯{Reset} {Bold}{Cyan}{opt.Label}{Reset}{detail}"
                    : $"    {opt.Label}{detail}");
            }
            foreach (var line in lines) Console.WriteLine(line);
            renderedLines = lines.Count;
        }

        try
        {
            Render();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow or ConsoleKey.K:
                        index = (index - 1 + options.Count) % options.Count;
                        Render();
                        break;
                    case ConsoleKey.DownArrow or ConsoleKey.J:
                        index = (index + 1) % options.Count;
                        Render();
                        break;
                    case ConsoleKey.Home:
                        index = 0;
                        Render();
                        break;
                    case ConsoleKey.End:
                        index = options.Count - 1;
                        Render();
                        break;
                    case ConsoleKey.Enter:
                        for (var i = 0; i < renderedLines; i++)
                            Console.Out.Write("\x1b[1A\r\x1b[2K");
                        renderedLines = 0;
                        Console.WriteLine($"  {Green}✓{Reset} {Bold}{title}{Reset} {Muted}›{Reset} {Cyan}{options[index].Label}{Reset}");
                        return (true, options[index].Value);
                    case ConsoleKey.Escape or ConsoleKey.Q:
                        for (var i = 0; i < renderedLines; i++)
                            Console.Out.Write("\x1b[1A\r\x1b[2K");
                        renderedLines = 0;
                        return (false, default!);
                    default:
                        if (key.KeyChar is >= '1' and <= '9')
                        {
                            var num = key.KeyChar - '1';
                            if (num < options.Count)
                            {
                                index = num;
                                Render();
                            }
                        }
                        break;
                }
            }
        }
        finally
        {
            Console.Out.Write("\x1b[?25h");
        }
    }

    public static bool Confirm(string question, string yesLabel = "Yes, proceed", string noLabel = "No, cancel", bool defaultYes = false)
    {
        var (selected, value) = Select(
            question,
            [
                new SelectOption<bool>(noLabel, false),
                new SelectOption<bool>(yesLabel, true)
            ],
            initialIndex: defaultYes ? 1 : 0);
        return selected && value;
    }

    public static string? Prompt(string label, string? defaultValue = null, Func<string, string?>? validate = null, string? placeholder = null)
    {
        EnsureCursorRestoredOnExit();
        while (true)
        {
            var hint = defaultValue is { Length: > 0 } ? $" {Muted}[{defaultValue}]{Reset}"
                : placeholder is { Length: > 0 } ? $" {Muted}({placeholder}){Reset}"
                : "";
            Console.Out.Write($"  {Cyan}?{Reset} {Bold}{label}{Reset}{hint}: ");
            var line = ReadLineWithEscape(out var cancelled);
            if (cancelled)
            {
                Console.WriteLine();
                return null;
            }
            var value = string.IsNullOrWhiteSpace(line) && defaultValue is not null ? defaultValue : (line ?? "").Trim();
            if (validate?.Invoke(value) is { } error)
            {
                Console.WriteLine($"    {Red}✗ {error}{Reset}");
                continue;
            }
            Console.Out.Write("\x1b[1A\r\x1b[2K");
            Console.WriteLine($"  {Green}✓{Reset} {Bold}{label}{Reset} {Muted}›{Reset} {Cyan}{value}{Reset}");
            return value;
        }
    }

    public static string? PromptPassword(string label = "Password", bool confirm = false, int minLength = 10)
    {
        EnsureCursorRestoredOnExit();
        while (true)
        {
            Console.Out.Write($"  {Cyan}?{Reset} {Bold}{label}{Reset} {Muted}(min {minLength} chars, Esc to cancel){Reset}: ");
            var first = ReadMaskedLine(out var cancelled);
            Console.WriteLine();
            if (cancelled) return null;
            if (first.Length < minLength)
            {
                Console.WriteLine($"    {Red}✗ Password must be at least {minLength} characters.{Reset}");
                continue;
            }
            if (confirm)
            {
                Console.Out.Write($"  {Cyan}?{Reset} {Bold}Confirm password{Reset}: ");
                var second = ReadMaskedLine(out var cancelConfirm);
                Console.WriteLine();
                if (cancelConfirm) return null;
                if (first != second)
                {
                    Console.WriteLine($"    {Red}✗ Passwords do not match. Try again.{Reset}");
                    continue;
                }
                Console.Out.Write("\x1b[1A\r\x1b[2K");
            }
            Console.Out.Write("\x1b[1A\r\x1b[2K");
            Console.WriteLine($"  {Green}✓{Reset} {Bold}{label}{Reset} {Muted}›{Reset} {Cyan}{new string('•', Math.Min(first.Length, 12))}{Reset}");
            return first;
        }
    }

    public static void Pause(string message = "Press Enter to return to the menu...")
    {
        if (!IsInteractive) return;
        Console.WriteLine();
        Console.Out.Write($"  {Muted}{message}{Reset}");
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Enter or ConsoleKey.Escape or ConsoleKey.Spacebar or ConsoleKey.Q) break;
        }
        Console.Out.Write("\r\x1b[2K");
    }

    private static string? ReadLineWithEscape(out bool cancelled)
    {
        cancelled = false;
        if (!IsInteractive) return Console.ReadLine();

        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
            {
                cancelled = true;
                return null;
            }
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    Console.Out.Write("\b \b");
                }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                Console.Out.Write(key.KeyChar);
            }
        }
    }

    private static string ReadMaskedLine(out bool cancelled)
    {
        cancelled = false;
        if (!IsInteractive) return Console.ReadLine() ?? "";

        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
            {
                cancelled = true;
                return "";
            }
            if (key.Key == ConsoleKey.Enter) return buffer.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    Console.Out.Write("\b \b");
                }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                Console.Out.Write('•');
            }
        }
    }

    private static int VisibleLength(string text) => AnsiPattern().Replace(text, "").Length;

    private static string TruncateVisible(string text, int maxVisible)
    {
        var plain = AnsiPattern().Replace(text, "");
        if (plain.Length <= maxVisible) return text;
        return plain[..Math.Max(0, maxVisible - 1)] + "…";
    }

    [GeneratedRegex(@"\x1b\[[0-9;?]*[a-zA-Z]")]
    private static partial Regex AnsiPattern();
}
