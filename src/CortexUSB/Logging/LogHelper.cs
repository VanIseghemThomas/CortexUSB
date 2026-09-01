using Serilog;

namespace CortexUSB.Logging
{
    public static class LogHelper
    {
        public static void Debug(string context, string message)
        {
            Log.ForContext("SourceContext", context)
               .Debug(message);
        }

        public static void Info(string context, string message)
        {
            Log.ForContext("SourceContext", context)
               .Information(message);
        }

        public static void Warning(string context, string message)
        {
            Log.ForContext("SourceContext", context)
               .Warning(message);
        }

        public static void Error(string context, string message)
        {
            Log.ForContext("SourceContext", context)
               .Error(message);
        }

        public static void Error(string context, Exception ex, string message)
        {
            Log.ForContext("SourceContext", context)
               .Error(ex, message);
        }
    }
}