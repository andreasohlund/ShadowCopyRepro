using NServiceBus;
using System;
using System.Threading;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Initializing");

        var ec = new EndpointConfiguration("MyEndpoint");

        ec.UseTransport<LearningTransport>();

        var endpoint = Endpoint.Start(ec).GetAwaiter().GetResult();
        Console.WriteLine("Running");

        Thread.Sleep(TimeSpan.FromSeconds(10));

        endpoint.Stop().GetAwaiter().GetResult();

        Console.WriteLine("Stopped");
    }
}

public class SomeThingThatGetScanned : INeedInitialization
{
    public void Customize(EndpointConfiguration configuration)
    {
        Console.WriteLine("Scanned type invoked");
    }
}