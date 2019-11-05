using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Provisioning.Client;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using Microsoft.Azure.Devices.Shared;
using Microsoft.VisualBasic.CompilerServices;
using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Bell.Device
{
    class Program
    {
        
        private static TimeSpan _sendInterval = TimeSpan.FromSeconds(60);

        //these objects will be reused frequently, so create them once rather than
        //creating them on each loop
        private static RandomNumberGenerator _rng = RandomNumberGenerator.Create();
        private static byte[] _fileData = new byte[1024 * 100]; //100KB


        static async Task Main(string[] args)
        {
            string deviceId = Environment.GetEnvironmentVariable("deviceId") ?? GenerateRandomDeviceId("Aircraft");

            /*
            When the device starts up, there are two different strategies you can use. One strategy
            says that a device will aways do the DPS provisioning workflow on startup, and the
            other strategy says that the device will do the DPS provisioning strategy once, get
            the Iot Hub connection information, and then on subsequent startups ignore DPS and
            just connect directly to the assigned Iot Hub
            
            Why would you use the "always connect to DPS on startup" strategy?
            You would do this to help plan for changes down the road.  Once a device has been
            enrolled by DPS it will be assigned a IoT Hub. In other words, the device does not
            know before hand which Iot Hub it will be using. The Iot Hub will by dynamically
            assigned when the device is provisioned. The device will then start sending
            data to the assigned IoT Hub. At some point, administrators might decide to reassign 
            a device to a different IoT Hub. If the device always connects through DPS at startup,
            then DPS can automatically reprovision that device to the new IoT Hub behind the scenes,
            and everything keeps working. Again, this works because the device is letting DPS tell
            it which Iot Hub to use on startup.

            Why would you use the "connec to to DPS once, and then Iot Hub always after" strategy?
            You would do this if you want to minimize interactions with DPS AND your deployment
            strategy is relatively static (in other words, once a device has been assigned to
            an Iot Hub, you don't ever plan on changing it).  This methods is more direct, in that
            it removes DPS from the equation after the first provision, but it is more limiting in
            that you would have to communicate any Iot Hub changes to the device in other ways
            */
            Console.WriteLine($"Provisioning device {deviceId}");
            EnrollmentDetails enrollment = await EnrollDeviceAsync(deviceId);

            //once enrolled, use the credentials created in the enrollment process to connect to IotHub
            Console.WriteLine($"Connecting to Iot Hub-{enrollment.IotHubEndpoint}");
            using (DeviceClient client = DeviceClient.Create(enrollment.IotHubEndpoint, enrollment.Authentication))
            {
                //When a device first boots up, it is a good idea for the device to go read the 
                //device twin information to pull any configuration values it may need for it's operation
                Console.WriteLine("Reading device twin information to set configuration values");
                await ReadDeviceConfigurationAsync(client);

                //now start processing data. In this example, the "device" code will run in a 
                //loop forever, pushing files/messages to IotHub
                while (true)
                {
                    Console.WriteLine("Sending a telemetry message to the Iot Hub");
                    await SendMessageAsync(client);
                    Console.WriteLine("Message sent");

                    Console.WriteLine($"Uploading file data");
                    await UploadFileAsync(client, deviceId);
                    Console.WriteLine("File upload completed");

                    Console.WriteLine();
                    Console.WriteLine($"Waiting {_sendInterval.TotalSeconds} seconds before repeating");
                    await Task.Delay(_sendInterval);
                }
            }
        }
        static string GenerateRandomDeviceId(string prefix)
        {
            byte[] buffer = new byte[2];
            _rng.GetNonZeroBytes(buffer);

            //conver the byte array to an unsigned 16 bit number
            ushort index = BitConverter.ToUInt16(buffer);
            return $"{prefix}{index:00000}";
        }
        static async Task ReadDeviceConfigurationAsync(DeviceClient client)
        {
            Twin twin = await client.GetTwinAsync();


            //see if an interval was specified in the device twin
            string interval = twin.Properties.Desired["SendInterval"];
            if (string.IsNullOrEmpty(interval) == false && TimeSpan.TryParse(interval, out TimeSpan intervalValue) == true)
            {
                Console.WriteLine($"A send interval has been configured for this device - {interval}");
                _sendInterval = intervalValue;
            }

            //a device can also report runtime information back to IotHub. In this example, the device
            //will report back to IotHub the version of the application that is executing
            TwinCollection reported = new TwinCollection();
            reported["SoftwareVersion"] = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            reported["CpuCount"] = Environment.ProcessorCount;

            await client.UpdateReportedPropertiesAsync(reported);

            //setup a call back to be notified if the desired properties change while this app is running
            await client.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertyUpdated, null);
        }
        static async Task SendMessageAsync(DeviceClient client)
        {
            //send a message (to simulate sending telemetry)
            byte[] body = Encoding.UTF8.GetBytes("This is a message being sent via IotHub");
            Message message = new Message(body);

            //set some metadata on the message
            message.Properties.Add("AircraftId", "123456");
            message.Properties.Add("DeviceSerialNumber", "ABC12345");

            await client.SendEventAsync(message);
        }
        static async Task UploadFileAsync(DeviceClient client, string deviceId)
        {
            //simulate uploading a file
            Console.WriteLine("Generating random file data to upload");

            //Each file would ideally have log/telemetry data. I don't have a real log file, so
            //I'm just generating some random data and using that as the file contents.
            _rng.GetBytes(_fileData);

            //Generate a file name. We will use a filename that includes a timestamp to
            //prevent collisions. By default, the IotHub client will overwrite a file in the blob
            //storage account if one already exists with the name, so a timestamp in the name should
            //prevent it
            DateTime now = DateTime.Now;
            string timestamp = $"{now.Year}{now.Month:00}{now.Day:00}_{now.Hour:00}{now.Minute:00}{now.Second:00}";
            string fileName = $"{deviceId}_data_{timestamp}.bin";

            //upload the file to blob storage using IotHub client
            using (MemoryStream fileStream = new MemoryStream(_fileData))
                await client.UploadToBlobAsync(fileName, fileStream);
        }
        static Task OnDesiredPropertyUpdated(TwinCollection desiredProperties, object userContext)
        {
            //this function is called when the desired properties are changed at the IotHub. IotHub
            //will notify our code (via the sdk), so that we can react to those changed. If your code
            //only runs for short period of times, then you may not be interested in the notifications
            //because your code will pick up the changes on the next execution. If you code is long running
            //(such as background processes on a device), then you would want to listen for these notifications
            //so that your app can make changes at run time.

            //In our case, we are interested if someone changes the SendInterval property, which dictates
            //the delay between sending data to IotHub from this application. If it is changed, we want
            //our code to update itself to reflect the change, while it is running

            //see if the Send Interval property was set
            if (desiredProperties["SendInterval"] != null)
            {
                string value = desiredProperties["SendInterval"];

                //try to parse the string value into a TimeSpan object
                if (TimeSpan.TryParse(value, out TimeSpan interval) == true)
                {
                    TimeSpan previous = _sendInterval;
                    _sendInterval = interval;

                    Console.WriteLine($"SendInterval has been updated from {previous.TotalSeconds} secs, to {_sendInterval.TotalSeconds} secs");
                }
            }
            return Task.CompletedTask;
        }
        static async Task<EnrollmentDetails> EnrollDeviceAsync(string deviceId)
        {
            //we need a couple of parameters to start. How you get these values is up to you.
            //What is important is that the enrollment keys (Primary & secondary) should
            //be considered as secrets and stored as such. Just like certificate, it
            //will be the responsibility of Bell to deploy these credentials to the device
            //and keep them up to date
            string dpsEndpoint = Environment.GetEnvironmentVariable("dpsEndpoint") ?? "global.azure-devices-provisioning.net";
            string dpsIdScope = Environment.GetEnvironmentVariable("idscope") ?? "{Insert ID Scope Here}";
            string enrollmentKeyPrimary = Environment.GetEnvironmentVariable("enrollmentKeyPrimary") ?? "{Insert Primary Enrollment Key Here}";
            string enrollmentKeySecondary = Environment.GetEnvironmentVariable("enrollmentKeySecondary") ?? "{Insert Seconday Enrollment Key Here}";

            //first, we need to create a device specific attestation key, derived from
            //the enrollment key. These attestation keys will ultimately become the credentials
            //for the device into Iot Hub when provisioned. They also serve as a way for 
            //the DPS service to validate the device is trusted, without actually having to
            //send the enrollment group key across the network
            string attestationKeyPrimary = ComputeDeviceAttestationKey(enrollmentKeyPrimary, deviceId);
            string attestationKeySecondary = ComputeDeviceAttestationKey(enrollmentKeySecondary, deviceId);

            //now, we can attempt to register this device with DPS
            using (SecurityProviderSymmetricKey keyProvider = new SecurityProviderSymmetricKey(deviceId, attestationKeyPrimary, attestationKeySecondary))
            {
                using (ProvisioningTransportHandlerAmqp transport = new ProvisioningTransportHandlerAmqp(TransportFallbackType.TcpOnly))
                {
                    ProvisioningDeviceClient provisioningClient = ProvisioningDeviceClient.Create(dpsEndpoint, dpsIdScope, keyProvider, transport);
                    DeviceRegistrationResult result = await provisioningClient.RegisterAsync();

                    switch (result.Status)
                    {
                        case ProvisioningRegistrationStatusType.Assigned:
                            //device was successfully assigned by dps to an Iot Hub
                            return new EnrollmentDetails()
                            {
                                DeviceId = deviceId,
                                IotHubEndpoint = result.AssignedHub,
                                Authentication = new DeviceAuthenticationWithRegistrySymmetricKey(deviceId, attestationKeyPrimary)
                            };
                        case ProvisioningRegistrationStatusType.Assigning:
                        case ProvisioningRegistrationStatusType.Disabled:
                        case ProvisioningRegistrationStatusType.Failed:
                        case ProvisioningRegistrationStatusType.Unassigned:
                        default:
                            return null;
                    }
                }
            }
        }
        static string ComputeDeviceAttestationKey(string enrollmentKey, string deviceId)
        {
            //the enrollment key is a base 64 representation of a binary key
            byte[] enrollmentKeyBytes = Convert.FromBase64String(enrollmentKey);

            //convert the device id to bytes
            byte[] deviceIdBytes = Encoding.UTF8.GetBytes(deviceId);

            //we create a device specific attestation key by hashing the device id
            //using the enrollment key
            using (var hmac = new HMACSHA256(enrollmentKeyBytes))
            {
                byte[] attestationKeyBytes = hmac.ComputeHash(deviceIdBytes);
                string attestationKey = Convert.ToBase64String(attestationKeyBytes);

                return attestationKey;
            }
        }
    }

    //Simple classes to hold the connection details to Iot Hub
    class EnrollmentDetails
    {
        public string DeviceId { get; set; }
        public IAuthenticationMethod Authentication { get; set; }
        public string IotHubEndpoint { get; set; }
    }
}
