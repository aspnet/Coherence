using System;

namespace CoherenceBuild
{
    public class Log
    {
        public static void WriteWarning(string value, params object[] args)
        {
            Console.WriteLine(value, args);
        }

        public static void WriteError(string value, params object[] args)
        {
            if (Environment.GetEnvironmentVariable("TEAMCITY_VERSION") != null)
            {
                value = value.Replace("|", "||")
                             .Replace("'", "|'")
                             .Replace("\r", "|r")
                             .Replace("\n", "|n")
                             .Replace("]", "|]");
                Console.Error.WriteLine("##teamcity[message text='" + value + "' status='ERROR']", args);
            }
            else
            {
                Console.Error.WriteLine(value, args);
            }
        }
    }
}
