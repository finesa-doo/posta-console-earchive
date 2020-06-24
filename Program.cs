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
            log.Info($"Is devel: {options.IsDevelopment}.");
            var tokenClient = GetAuthClient(options.FindCertificateByValue, options.IsDevelopment);
            if (tokenClient == null)
            {
                return 6;
            }

            foreach (var r in dt.Rows)
            {
                try
                {
                    var inputFile = r["files"].Split(';');

                    if (inputFile.Count() != 1)
                    {
                        throw new Exception("multiple files not supported");
                    }

                    var fi = new FileInfo(inputFile[0]);
                    if (!fi.Exists)
                    {
                        log.Error(string.Format("ERROR: file {0} doesn't exist. Skipping...", fi.Name));
                        errors++;
                        continue;
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
                    Guid guidTransaction = SendDocument(tokenClient, options.IsDevelopment, fi, naziv, koda, nazivDobavitelja, sifraDobavitelja,
                                            davcnaStevilkaDobavitelja, stevilkaRacuna, datumIzdajeRacuna,
                                            leto, node);

                    r["guid"] = guidTransaction.ToString();
                    log.Info("Sent document with GUID:" + guidTransaction.ToString() + " Code:" + koda);
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"Error in loop: {ex.Message}");
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

        private static EAClient GetClient(EAAuth tokenClient, bool isDevelopment)
        {
            return new EAClient(isDevelopment ? "https://tstearchps.posta.si/WEBAPI/api" : "https://earchps.posta.si/WEBAPI/api");
        }

        private static Guid SendDocument(EAAuth tokenClient, bool isDevelopment, FileInfo fi, string Naziv, string Koda, string NazivDobavitelja, string SifraDobavitelja,
            string DavcnaStevilkaDobavitelja, string StevilkaRacuna, DateTime DatumIzdajeRacuna, int Leto, string Node)
        {
            var client = GetClient(tokenClient, isDevelopment);
            var token = tokenClient.GetBearerToken();
            var nodes = client.GetAllNodes(token);
            var node = nodes.Where(n => n.Code == Node).FirstOrDefault();
            if (node == null)
            {
                throw new Exception("didn't find node using Node parameter");
            }

            var metas = client.GetCustomMetadataTypes(tokenClient.GetBearerToken());

            var aMetadata = new Metadata();
            aMetadata.NodeGuid = node.NodeGuid;
            aMetadata.FileName = fi.Name.Substring(0, fi.Name.Length - fi.Extension.Length);
            aMetadata.FileExtension = fi.Extension;
            aMetadata.DigitalSignatureType = PS.EA.SDK.Enums.DigitalSignatureType.None;

            aMetadata.CreatedDate = DatumIzdajeRacuna;
            aMetadata.Title = Koda;
            aMetadata.Code = Koda;
            aMetadata.Receiver = NazivDobavitelja;
            aMetadata.CustomMetadatas = new List<CustomMetadata>();

            foreach (var md in metas)
            {
                CustomMetadata aMd = null;
                if (md.Label == "Šifra partnerja")
                {
                    aMd = new CustomMetadata { CustomMetadataType = new CustomMetadataType { CustomMetadataTypeGuid = md.CustomMetadataTypeGuid }, Value = SifraDobavitelja };
                }
                if (md.Label == "Davčna številka partnerja")
                {
                    aMd = new CustomMetadata { CustomMetadataType = new CustomMetadataType { CustomMetadataTypeGuid = md.CustomMetadataTypeGuid }, Value = DavcnaStevilkaDobavitelja };
                }
                if (md.Label == "Številka računa")
                {
                    aMd = new CustomMetadata { CustomMetadataType = new CustomMetadataType { CustomMetadataTypeGuid = md.CustomMetadataTypeGuid }, Value = StevilkaRacuna };
                }
                if (md.Label == "Datum izdaje računa")
                {
                    // aMd = new CustomMetadata { CustomMetadataType = new CustomMetadataType { CustomMetadataTypeGuid = md.CustomMetadataTypeGuid }, Value = DatumIzdajeRacuna.ToString("yyyy-MM-ddTHH\\:mm\\:ss.fffffffzzz") };
                }
                if (md.Label == "Leto")
                {
                    aMd = new CustomMetadata { CustomMetadataType = new CustomMetadataType { CustomMetadataTypeGuid = md.CustomMetadataTypeGuid }, Value = Leto.ToString() };
                }
                if (aMd != null)
                {
                    aMetadata.CustomMetadatas.Add(aMd);
                }
            }

            byte[] bytes = File.ReadAllBytes(fi.FullName);
            return client.AddDocument(token,  bytes, aMetadata);
        }

        private static EAAuth GetAuthClient(string findCertificateByValue, bool isDevelopment)
        {
            EAAuth tokenClient = null;
            try
            {
                var cert = GetCertificate(findCertificateByValue);
                tokenClient = new EAAuth(isDevelopment ? "https://tstearchps.posta.si/WEBAUTH/Token" : "https://earchps.posta.si/WEBAUTH/Token", cert);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"AUTH ERROR: {ex.Message}");
            }
            return tokenClient;
        }

        private static int ListNodesAndReturnExitCode(ListNodesOptions options)
        {
            log.Info($"Is devel: {options.IsDevelopment}.");

            var tokenClient = GetAuthClient(options.FindCertificateByValue, options.IsDevelopment);
            if (tokenClient == null)
            {
                return 6;
            }

            var client = GetClient(tokenClient, options.IsDevelopment);
            try
            {
                var nodes = client.GetAllNodes(tokenClient.GetBearerToken());
                if (nodes == null || nodes.Count() == 0)
                {
                    log.Info($"No nodes found.");
                }
                foreach(var n in nodes)
                {
                    log.Info($"{n.Code} {n.NodeGuid} {n.Label}");
                }
                log.Info($"------------------------");

                var metas  = client.GetCustomMetadataTypes(tokenClient.GetBearerToken());
                foreach (var m in metas)
                {
                    log.Info($"{m.Label} {m.DataType} {m.CustomMetadataTypeGuid}");
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, $"GetAllNodes ERROR: {ex.Message}");
                return 7;
            }
            return 0;
        }

        private static int RunSendAndReturnExitCode(SendLDocOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.InputFile))
            {
                return 3; // this shouldn't happen anyway, because parameter is already required
            }
            var fi = new FileInfo(options.InputFile);
            if (!fi.Exists)
            {
                log.Error(string.Format("ERROR: file {0} doesn't exist.", fi.Name));
                return 4;
            }
            var tokenClient = GetAuthClient(options.FindCertificateByValue, options.IsDevelopment);
            if (tokenClient == null)
            {
                return 6;
            }

            log.Info($"SendLDoc Method...\nWith file: {options.InputFile}");
            Guid guidTransaction = Guid.Empty;
            try
            {
                guidTransaction = SendDocument(tokenClient, options.IsDevelopment, fi, options.Naziv, options.Koda, options.NazivDobavitelja, options.SifraDobavitelja,
                                        options.DavcnaStevilkaDobavitelja, options.StevilkaRacuna, options.DatumIzdajeRacuna,
                                        options.Leto, options.Node);
            }
            catch (Exception ex)
            {
                log.Error(ex);
                return 100;
            }
            log.Info("Transaction GUID: " + guidTransaction);
            return 0;
        }

        private static X509Certificate2 GetCertificate(string findByValue)
        {
            using (X509Store storex = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                storex.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection certificatesx = storex.Certificates.Find(X509FindType.FindBySerialNumber, findByValue, true);

                if (certificatesx == null || certificatesx.Count != 1)
                {
                    throw new Exception("Certificate not found.");
                }

                X509Certificate2 cert = certificatesx[0];
                
                storex.Close();
                return cert;
            }
        }

        static int Main(string[] args)
        {
            log.Info("Start.");
            var parser = new Parser(with => { with.HelpWriter = Console.Out; with.EnableDashDash = true; });

            // sendfromcsv --file=test.csv

            var result = parser.ParseArguments<SendLDocOptions, SendLDocCSVOptions, ListNodesOptions>(args);
            var exitCode = result.MapResult(
                    (SendLDocOptions opts) => RunSendAndReturnExitCode(opts),
                    (SendLDocCSVOptions opts) => RunSendFromCsvAndReturnExitCode(opts),
                    (ListNodesOptions opts) => ListNodesAndReturnExitCode(opts),
                    errs => 1
                );
            
            log.Info("Press any key.");
            Console.ReadKey();
            return exitCode;
        }
    }
}
