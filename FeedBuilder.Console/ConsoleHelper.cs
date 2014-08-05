using System;

namespace FeedBuilder
{
    /// <summary>
    /// Static helper class used to color output to the console
    /// </summary>
    static class ConsoleHelper
    {
        public static void Success(string msg, params object[] args)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(msg, args);
            Console.ResetColor();
        }

        public static void Info(string msg, params object[] args)
        {
            Console.WriteLine(msg, args);
        }

        public static void Error(string msg, params object[] args)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(msg, args);
            Console.ResetColor();
        }

        public static void Warn(string msg, params object[] args)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(msg, args);
            Console.ResetColor();
        }
    }
}