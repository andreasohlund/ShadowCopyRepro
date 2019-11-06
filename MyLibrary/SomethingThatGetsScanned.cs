using NServiceBus;
using System;

namespace MyLibrary
{
    public class SomeThingThatGetScanned : INeedInitialization
    {
        public void Customize(EndpointConfiguration configuration)
        {
            Console.WriteLine("Scanned type invoked");
        }
    }
}
