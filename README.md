Web.Providers.MongoDBSessionStateStore
-----------------------------------------------

MongoDB Session State Provider
Custom ASP.NET Session State Provider using MongoDB as the state store.

Based on (read copied a lot from): 
https://github.com/AdaTheDev/MongoDB-ASP.NET-Session-State-Store

Reference:
http://msdn.microsoft.com/en-us/library/ms178587.aspx
http://msdn.microsoft.com/en-us/library/ms178588.aspx



Session state is stored in a "Sessions" collection within a "SessionState" database.      
Example session document:

    {
        "_id" : "bh54lskss4ycwpreet21dr1h",
        "ApplicationName" : "/",
        "Created" : ISODate("2011-04-29T21:41:41.953Z"),
        "Expires" : ISODate("2011-04-29T22:01:41.953Z"),
        "LockDate" : ISODate("2011-04-29T21:42:02.016Z"),
        "LockId" : 1,
        "Timeout" : 20,
        "Locked" : true,
        "Items" : "AQAAAP/8EVGVzdAgAAAABBkFkcmlhbg==",
        "Flags" : 0
    }
     

Exception Handling:
------------------

If the provider encounters an exception when working with the data source, it writes the details of the exception to the Application Event Log instead of returning the exception to the ASP.NET application. This is done as a security measure to avoid private information about the data source from being exposed in the ASP.NET application.

The sample provider specifies an event Source property value of "MongoSessionStateStore." Before your ASP.NET application will be able to write to the Application Event Log successfully, you will need to create the following registry key:
   
    HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Eventlog\Application\MongoSessionStateStore
     
If you do not want the sample provider to write exceptions to the event log, then you can set the custom writeExceptionsToEventLog attribute to false in the Web.config file.

more: http://stackoverflow.com/questions/1610189/security-exception-when-writting-to-an-eventlog-from-an-asp-net-mvc-application?lq=1



Expired Sessions Cleanup:
-------------------------
     
The session-state store provider does not provide support for the Session_OnEnd event, it does not automatically clean up expired session-item data. It is recommended that you periodically delete expired session information from the data store with the following code.:

    db.Sessions.remove({"Expires" : {$lt : new Date() }})

more: http://stackoverflow.com/questions/1042881/why-session-end-event-not-raised-when-stateprovider-is-not-inproc


     
Example web.config settings:
----------------------------

    <connectionStrings>
        <add name="MongoDBSessionState" connectionString="mongodb://localhost" />
    </connectionStrings>
    <system.web>
        <sessionState
            timeout="1440"
            cookieless="false"
            mode="Custom"
            customProvider="MongoSessionStateProvider">
            <providers>
                <add name="MongoSessionStateProvider"
                    type="Quintsys.Web.Providers.MongoDBSessionStateStore"
                    connectionStringName="MongoDBSessionState"
                    fsync="false"
                    replicasToWrite="0"
                    writeExceptionsToEventLog="false" />
            </providers>
        </sessionState>
    </system.web>


> replicasToWrite
Interpreted as the number of replicas to write to, in addition to the primary (in a replicaset environment).


    replicasToWrite = 0, will wait for the response from writing to the primary node. 
    replicasToWrite > 0 will wait for the response having written to ({replicasToWrite} + 1) nodes
