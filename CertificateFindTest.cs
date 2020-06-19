using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;

namespace FinesaPosta
{
    class CertificateFindTest
    {
        public  void TesT()
        {
            X509Store storex = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            storex.Open(OpenFlags.ReadOnly);
            X509Certificate2Collection certificatesx = storex.Certificates.Find(X509FindType.FindBySerialNumber, //certificateName,
                        "3B48F17B",
                //"86 51 c0 35 93 ae 6a 9d 0d 69 86 44 3b 8e 11 4f 19 a9 8c a1",
                        true);

            X509Certificate cert = certificatesx[0];

            storex.Close();

            string results = cert.GetSerialNumberString();


            Console.WriteLine(cert.Subject);

            Console.WriteLine(BitConverter.ToString(cert.GetSerialNumber()));
            // Display the value to the console.
            Console.WriteLine(results);
            Console.ReadKey();
        }
        
    }
}
