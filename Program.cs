using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Xml;
using CommandLine;
using System.Xml.Linq;
using DataAccess;
using System.Globalization;
using NLog;
using PS.EA.SDK;
using System.ServiceModel.Configuration;
using PS.EA.SDK.Model;

namespace FinesaPosta
{
    class Program
    {
        private static Logger log = LogManager.GetCurrentClassLogger();

        /*private static int RunGetSchemaAndReturnExitCode(GetSendLDocSchemaOptions options)
        {
            var x = objWS_S.GetSendLDocSchema();
            log.Debug(PrettyXml(x.OuterXml));
            return 0;
        }*/

        private static int RunSendFromCsvAndReturnExitCode(SendLDocCSVOptions options)
        {
            var provider = CultureInfo.CreateSpecificCulture("en-US");
            if (string.IsNullOrWhiteSpace(options.CsvListFileName))
            {
                return 3; // this shouldn't happen anyway, because parameter is already required
            }
            var csvListFile = new FileInfo(options.CsvListFileName);
            if (!csvListFile.Exists)
            {
                log.Error(string.Format("ERROR: file {0} doesn't exist.", csvListFile.Name));
                return 4; 
            }
            if (!csvListFile.Extension.ToLower().Equals(".csv"))
            {
                log.Error(string.Format("ERROR: file {0} should have .csv extnsion.", csvListFile.Name));
                return 4; 
            }

            MutableDataTable dt;
            try
            {
                dt = DataTable.New.ReadCsv(csvListFile.FullName);
                int totalRows = dt.NumRows;
                if (totalRows < 1)
                {
                    log.Fatal(string.Format("ERROR: file {0} doesn't contain any rows.", csvListFile.Name));
                    return 4;     
                }
                const int requiredColumns = 11;
                if (dt.Columns.Count() != requiredColumns)
                {
                    log.Fatal(string.Format("ERROR: file {0} should contain exactly {1} columns.", csvListFile.Name, requiredColumns));
                    return 4;     
                }
                log.Info("Found " + totalRows + " rows.");
            }
            catch (Exception ex) 
            {
                log.Fatal(ex);
                return 22;
            }
            var errors = 0;

            foreach (var r in dt.Rows)
            {
                try
                {
                    var inputFiles = r["files"].Split(';');
                    var files = new List<FileInfo>();
                    foreach (var f in inputFiles)
                    {
                        var fi = new FileInfo(f);
                        if (!fi.Exists)
                        {
                            log.Error(string.Format("ERROR: file {0} doesn't exist. Skipping...", fi.Name));
                            errors++;
                            continue;  
                        }
                        files.Add(fi);
                    }

                    var naziv = r["naziv"];
                    var koda = r["koda"];
                    var nazivDobavitelja = r["nazivdobavitelja"];
                    var sifraDobavitelja = r["sifradobavitelja"];
                    var davcnaStevilkaDobavitelja = r["davcnastevilkadobavitelja"];
                    var stevilkaRacuna = r["stevilkaracuna"];
                    var node = r["Node"];
                    DateTime datumIzdajeRacuna = DateTime.ParseExact(r["datumizdajeracuna"].Trim(), "yyyy-dd-M", provider);
                    int leto = Int32.Parse(r["leto"]);

                    Guid guidTransaction = Guid.Empty;
                    guidTransaction = SendLogicaDocument(files, naziv, koda, nazivDobavitelja, sifraDobavitelja,
                                            davcnaStevilkaDobavitelja, stevilkaRacuna, datumIzdajeRacuna,
                                            leto, node, options.Debug);
                    r["guid"] = guidTransaction.ToString();
                    log.Info("Sent document with GUID:" + guidTransaction.ToString() + " Code:" + koda);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Error in loop: ");
                    errors++;
                }
            }

            try
            {
                var result = Path.Combine(csvListFile.DirectoryName, "out-" + csvListFile.Name);
                dt.SaveCSV(result); 
            }
            catch (Exception ex)
            {
                log.Error(ex, "Error saving: ");
                return 222;
            }

            if (errors > 0)
                return 111;
            else
                return 0;
        }

        private static Guid SendLogicaDocument(IEnumerable<FileInfo> files, string Naziv, string Koda, string NazivDobavitelja, string SifraDobavitelja, 
            string DavcnaStevilkaDobavitelja, string StevilkaRacuna, DateTime DatumIzdajeRacuna, int Leto, string Node, bool debug)
        {
            XNamespace posta = "http://schemas.posta.si/earchive/sendldoc.xsd";
            var PDocs = new XElement(posta + "PDocs");

            XElement root = new XElement(posta + "LDoc", null, new XAttribute("version", "1.0"),
                PDocs
            );

            foreach (var fi in files)
            {
                byte[] data = File.ReadAllBytes(fi.FullName);
                var fileGuid = Guid.NewGuid();

                var idString = "Id-" + fileGuid.ToString();

                PDocs.Add(new XElement(posta + "PDoc",
                    new XElement(posta + "Binary", "true"),
                    new XElement(posta + "DataBinary", Convert.ToBase64String(data), new XAttribute("Id", idString)),
                    new XElement(posta + "Filename", fi.Name),
                    new XElement(posta + "FileExtension", fi.Extension),
                    new XElement(posta + "FormatCode", "??"),
                    new XElement(posta + "Signed", false),
                    new XElement(posta + "Encrypted", false)
                ));
            }

            root.Add(new XElement(posta + "Nodes",
                new XElement(posta + "Node", 
                    new XElement(posta + "Code", Node))));

            root.Add(new XElement(posta + "Label", Naziv));
            root.Add(new XElement(posta + "Code", Koda));
            root.Add(new XElement(posta + "Version", 1));
            root.Add(new XElement(posta + "Subversion", 0));

            root.Add(new XElement(posta + "NazivDobavitelja", NazivDobavitelja));
            root.Add(new XElement(posta + "SifraDobavitelja", SifraDobavitelja));
            root.Add(new XElement(posta + "DavcnaStevilkaDobavitelja", DavcnaStevilkaDobavitelja));
            root.Add(new XElement(posta + "StevilkaRacuna", StevilkaRacuna));
            root.Add(new XElement(posta + "DatumIzdajeRacuna", DatumIzdajeRacuna));
            root.Add(new XElement(posta + "Leto", Leto));

            if (debug)
            {
                log.Debug(root.ToXmlElement().OuterXml);
            }

            // XmlElement final = GetElement("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root.ToXmlElement().OuterXml);
            throw new NotImplementedException();
            return Guid.NewGuid(); 
        }

        private static int RunSendAndReturnExitCode(SendLDocOptions options)
        {
            if (options.InputFiles.Count() < 1)
            {
                return 3; // this shouldn't happen anyway, because parameter is already required
            }
            var  files = new List<FileInfo>();
            foreach (var f in options.InputFiles)
            {
                var fi = new FileInfo(f); 
                if (!fi.Exists)
                {
                    log.Error(string.Format("ERROR: file {0} doesn't exist.", fi.Name));
                    return 4;
                }
                files.Add(fi);
            }

            log.Info(string.Format("SendLDoc Method...\nWith files: {0}", string.Join("; ", options.InputFiles.ToArray())));

            Guid guidTransaction = Guid.Empty;
            try
            {
                guidTransaction = SendLogicaDocument(files, options.Naziv, options.Koda, options.NazivDobavitelja, options.SifraDobavitelja,
                                        options.DavcnaStevilkaDobavitelja, options.StevilkaRacuna, options.DatumIzdajeRacuna, 
                                        options.Leto, options.Node, options.Debug);
            }
            catch (Exception ex)
            {
                log.Error(ex);
                return 100;
            }
            log.Info("Transaction GUID: " + guidTransaction);
            return 0;
        }

        private static byte[] GetRandomFile()
        {
            Random rnd = new Random();
            Byte[] b = new Byte[100];
            rnd.NextBytes(b);
            return b;
        }

        private static X509Certificate2 GetCertificate()
        {
            using (X509Store storex = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                storex.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection certificatesx = storex.Certificates.Find(X509FindType.FindBySerialNumber, //certificateName,
                            "3B48F17B",
                            //"86 51 c0 35 93 ae 6a 9d 0d 69 86 44 3b 8e 11 4f 19 a9 8c a1",
                            true);

                X509Certificate2 cert = certificatesx[0];
                
                storex.Close();
                return cert;
            }
            // string results = cert.GetSerialNumberString();
        }

        private static int RunGetAndReturnExitCode(GetLDocOptions opts)
        {
            var cert = GetCertificate();
            var tokenClient = new EAAuth("https://tstearchps.posta.si/WEBAUTH/Token", cert);
            var client = new EAClient("https://tstearchps.posta.si/WEBAUTH/Token");
            var token = tokenClient.GetBearerToken();
            //var nodes = client.GetAllNodes(token);

            var aMetadata = new Metadata();
            aMetadata.Author = "Jože";
            aMetadata.FileName = "burek";
            aMetadata.FileExtension = "dat";
            aMetadata.NodeGuid = new Guid("85d28d6c-fe98-ea11-90f4-001dd8b720a0");

//            prejetiRacuni.

            var guid = client.AddDocument(token, GetRandomFile(), aMetadata);

            log.Info($"GET {guid}");
            

            //  https://tstearchps.posta.si/WEBAPI

            return 1;
        }

        static int Main(string[] args)
        {
            log.Info("Start.");
            var parser = new Parser(with => { with.HelpWriter = Console.Out; with.EnableDashDash = true; });

            // sendfromcsv --file=test.csv

            var result = parser.ParseArguments<SendLDocOptions, GetLDocOptions, SendLDocCSVOptions, GetSendLDocSchemaOptions>(args);
            var exitCode = result.MapResult(
                    (SendLDocOptions opts) => RunSendAndReturnExitCode(opts),
                    (GetLDocOptions opts) => RunGetAndReturnExitCode(opts),
                    (SendLDocCSVOptions opts) => RunSendFromCsvAndReturnExitCode(opts),
                    // (GetSendLDocSchemaOptions opts) => RunTest(opts),
                    errs => 1
                );
            
            log.Info("All done.");
            Console.ReadKey();
            return exitCode;
        }
    }
}
