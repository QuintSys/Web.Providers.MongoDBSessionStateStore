using System;
using System.Diagnostics;

namespace Quintsys.Web.Providers.MongoDBSessionStateStore
{
    public static class ExceptionExtensions
    {
        /// <summary>
        /// This is a helper function that writes exception detail to the event log. 
        /// Exceptions are written to the event log as a security measure to ensure 
        /// private database details are not returned to browser. 
        /// </summary>
        public static void WriteToEventLog(this Exception exception, string action, string eventSource, string eventLog)
        {
            const string format = "An exception occurred communicating with the data source.\n\nAction: {0}\n\nException: {1}";
            using (var log = new EventLog())
            {
                log.Source = eventSource;
                log.Log = eventLog;

                var message = string.Format(format, action, exception);
                log.WriteEntry(message);
            }
        }
    }
}