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

namespace FinesaPosta
{
    /*
    https://tstearhiv.posta.si/Default.aspx
     */


    class Program
    {
        private static Logger log = LogManager.GetCurrentClassLogger();

        private static EArchiveClient objWS_S;

        static string PrettyXml(string xml)
        {
            var stringBuilder = new StringBuilder();

            var element = XElement.Parse(xml);

            var settings = new XmlWriterSettings();
            settings.OmitXmlDeclaration = true;
            settings.Indent = true;
            settings.NewLineOnAttributes = true;

            using (var xmlWriter = XmlWriter.Create(stringBuilder, settings))
            {
                element.Save(xmlWriter);
            }

            return stringBuilder.ToString();
        }

        private static int RunGetSchemaAndReturnExitCode(GetSendLDocSchemaOptions options)
        {
            var x = objWS_S.GetSendLDocSchema();
            Console.WriteLine(PrettyXml(x.OuterXml));
            return 0;
        }

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
                Console.WriteLine("ERROR: file {0} doesn't exist.", csvListFile.Name);
                return 4; 
            }
            if (!csvListFile.Extension.ToLower().Equals(".csv"))
            {
                Console.WriteLine("ERROR: file {0} should have .csv extnsion.", csvListFile.Name);
                return 4; 
            }

            MutableDataTable dt;
            try
            {
                dt = DataTable.New.ReadCsv(csvListFile.FullName);
                int totalRows = dt.NumRows;
                if (totalRows < 1)
                {
                    Console.WriteLine("ERROR: file {0} doesn't contain any rows.", csvListFile.Name);
                    return 4;     
                }
                const int requiredColumns = 11;
                if (dt.Columns.Count() != requiredColumns)
                {
                    Console.WriteLine("ERROR: file {0} should contain exactly {1} columns.", csvListFile.Name, requiredColumns);
                    return 4;     
                }                
            }
            catch (Exception ex) 
            {
                log.Error("Error: ", ex);
                Console.WriteLine("Error: " + ex.Message);
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
                            Console.WriteLine("ERROR: file {0} doesn't exist. Skipping...", fi.Name);
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
                }
                catch (Exception ex)
                {
                    log.Error("Error in loop: ", ex);
                    Console.WriteLine("Error: " + ex.Message);
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
                Console.WriteLine("Error saving: " + ex.Message);
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
                Console.WriteLine(root.ToXmlElement().OuterXml);
            }

            XmlElement final = GetElement("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root.ToXmlElement().OuterXml);

            return objWS_S.SendLDoc(root.ToXmlElement());
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
                    Console.WriteLine("ERROR: file {0} doesn't exist.", fi.Name);
                    return 4;
                }
                files.Add(fi);
            }

            Console.WriteLine("SendLDoc Method...\n");
            Console.WriteLine("With files: {0}", string.Join("; ", options.InputFiles.ToArray()));

            Guid guidTransaction = Guid.Empty;
            try
            {
                guidTransaction = SendLogicaDocument(files, options.Naziv, options.Koda, options.NazivDobavitelja, options.SifraDobavitelja,
                                        options.DavcnaStevilkaDobavitelja, options.StevilkaRacuna, options.DatumIzdajeRacuna, 
                                        options.Leto, options.Node, options.Debug);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                return 100;
            }
            Console.WriteLine("Transaction GUID: " + guidTransaction);
            return 0;
        }

        private static XmlElement GetElement(string xml)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc.DocumentElement;
        }


        private static int RunGetAndReturnExitCode(GetLDocOptions opts)
        {
            Console.WriteLine("GET");
            return 1;
        }

        static int Main(string[] args)
        {
            var parser = new Parser(with => { with.HelpWriter = Console.Out; with.EnableDashDash = true; }); 
            try
            {
                objWS_S = new EArchiveClient("EArchiveDefaultTest");
                var isRunning = objWS_S.IsRunning();
                if (!isRunning) 
                {
                    Console.WriteLine("Connection to webservice failed. Service is not running.");
                    return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Connection to webservice failed. Is certificate configured properly?");
                Console.WriteLine(ex.Message);
                return 1;
            }

            var result = parser.ParseArguments<SendLDocOptions, GetLDocOptions, SendLDocCSVOptions, GetSendLDocSchemaOptions>(args);
            var exitCode = result.MapResult(
                    (SendLDocOptions opts) => RunSendAndReturnExitCode(opts),
                    (GetLDocOptions opts) => RunGetAndReturnExitCode(opts),
                    (SendLDocCSVOptions opts) => RunSendFromCsvAndReturnExitCode(opts),
                    (GetSendLDocSchemaOptions opts) => RunGetSchemaAndReturnExitCode(opts),
                    errs => 1
                );
            objWS_S.Close();
            Console.WriteLine("All done.");
            return exitCode;
        }
    }
}
