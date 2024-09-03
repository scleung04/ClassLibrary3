using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static System.Net.Mime.MediaTypeNames;
using System.Security.Policy;
using System.Xml.Linq;

namespace ClassLibrary3
{
    [Transaction(TransactionMode.Manual)]
    public class Class1 : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Path to the log file
            string logFilePath = "C:\\Users\\scleu\\Downloads\\2024 Summer Research\\Dynamo_Revit_Log.txt";

            try
            {
                // Get the current Revit application
                UIApplication uiApp = commandData.Application;
                Document doc = uiApp.ActiveUIDocument.Document;

                // Open the log file
                using (StreamWriter writer = new StreamWriter(logFilePath, true))
                {
                    bool saveChanges = false;

                    try
                    {
                        // Write success message to log file
                        writer.WriteLine(DateTime.Now.ToString() + ": Successfully accessed the open Revit file: " + doc.PathName);

                        // Perform any necessary processing here
                        // Check for the panel-circuit mismatch
                        bool mismatchFound = CheckPanelCircuitMismatch(doc, writer);

                        if (mismatchFound)
                        {
                            // Disconnect the panel automatically and log the action
                            using (Transaction trans = new Transaction(doc, "Disconnect Panel from Circuit"))
                            {
                                trans.Start();
                                try
                                {
                                    DisconnectPanelFromCircuit(doc, writer);
                                    saveChanges = true;
                                    writer.WriteLine(DateTime.Now.ToString() + ": Disconnected panel from circuit for file: " + doc.PathName);
                                    trans.Commit();
                                }
                                catch (Exception ex)
                                {
                                    trans.RollBack();
                                    writer.WriteLine(DateTime.Now.ToString() + ": An error occurred during the transaction for file " + doc.PathName + ": " + ex.Message);
                                    writer.WriteLine(ex.StackTrace);
                                }
                            }
                        }
                        else
                        {
                            // If no mismatch was found, mark saveChanges to true
                            saveChanges = true;
                        }

                        if (saveChanges)
                        {
                            // Save the document outside of any active transaction
                            SaveDocument(doc, writer);
                        }

                        // Export to IFC using Autodesk.IFC.Export.UI
                        ExportToIFC(doc, doc.PathName, writer);
                    }
                    catch (Exception ex)
                    {
                        // Write detailed error message to log file
                        writer.WriteLine(DateTime.Now.ToString() + ": An error occurred with file " + doc.PathName + ": " + ex.Message);
                        writer.WriteLine(ex.StackTrace);

                        // Automatically handle the error by disconnecting the panel
                        try
                        {
                            using (Transaction trans = new Transaction(doc, "Disconnect Panel from Circuit on Error"))
                            {
                                trans.Start();
                                DisconnectPanelFromCircuit(doc, writer);
                                saveChanges = true;
                                writer.WriteLine(DateTime.Now.ToString() + ": Automatically disconnected panel from circuit for file: " + doc.PathName);
                                trans.Commit();
                            }
                        }
                        catch (Exception innerEx)
                        {
                            writer.WriteLine(DateTime.Now.ToString() + ": An additional error occurred while trying to disconnect the panel for file " + doc.PathName + ": " + innerEx.Message);
                            writer.WriteLine(innerEx.StackTrace);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Log the operation cancellation
                using (StreamWriter writer = new StreamWriter(logFilePath, true))
                {
                    writer.WriteLine(DateTime.Now.ToString() + ": The operation was cancelled by the user.");
                }
            }
            catch (Exception ex)
            {
                using (StreamWriter writer = new StreamWriter(logFilePath, true))
                {
                    writer.WriteLine(DateTime.Now.ToString() + ": An error occurred: " + ex.Message);
                }
            }

            return Result.Succeeded;
        }

        private void SaveDocument(Document doc, StreamWriter writer)
        {
            try
            {
                doc.Save();
                writer.WriteLine($"{DateTime.Now}: Document saved successfully.");
            }
            catch (Exception ex)
            {
                writer.WriteLine($"{DateTime.Now}: An error occurred while saving the document: {ex.Message}");
                writer.WriteLine(ex.StackTrace);
            }
        }

        private bool CheckPanelCircuitMismatch(Document doc, StreamWriter writer)
        {
            bool mismatchFound = false;

            var panels = new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                         .OfClass(typeof(FamilyInstance))
                         .WhereElementIsNotElementType()
                         .OfType<FamilyInstance>()
                         .ToList();

            foreach (var panel in panels)
            {
                double totalLoad = panel.LookupParameter("Total Connected Load")?.AsDouble() ?? 0;
                double panelCapacity = panel.LookupParameter("Panel Capacity")?.AsDouble() ?? 0;

                if (totalLoad > panelCapacity)
                {
                    mismatchFound = true;
                    writer.WriteLine($"{DateTime.Now}: Mismatch found for panel {panel.Name}. Total load exceeds capacity.");
                }
            }

            return mismatchFound;
        }

        private void DisconnectPanelFromCircuit(Document doc, StreamWriter writer)
        {
            var panels = new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                         .OfClass(typeof(FamilyInstance))
                         .WhereElementIsNotElementType()
                         .OfType<FamilyInstance>()
                         .ToList();

            foreach (var panel in panels)
            {
                var electricalSystems = panel.MEPModel?.GetElectricalSystems();
                if (electricalSystems != null)
                {
                    foreach (var electricalSystem in electricalSystems)
                    {
                        using (var trans = new Transaction(doc, "Disconnect Panel from Circuit"))
                        {
                            trans.Start();
                            foreach (var connector in electricalSystem.ConnectorManager.Connectors.OfType<Connector>())
                            {
                                foreach (var connectedConnector in connector.AllRefs.OfType<Connector>())
                                {
                                    connector.DisconnectFrom(connectedConnector);
                                    writer.WriteLine($"{DateTime.Now}: Disconnected connector {connector.Owner.Id} from {connectedConnector.Owner.Id}");
                                }
                            }
                            if (ShouldDeleteElement(electricalSystem))
                            {
                                doc.Delete(electricalSystem.Id);
                                writer.WriteLine($"{DateTime.Now}: Deleted electrical system: {electricalSystem.Id.IntegerValue}");
                            }
                            trans.Commit();
                        }
                    }
                }
            }
        }

        private bool ShouldDeleteElement(Element element)
        {
            if (element is ElectricalSystem electricalSystem)
            {
                List<Connector> loadConnectors = new List<Connector>();

                foreach (Element e in electricalSystem.Elements)
                {
                    if (e is FamilyInstance familyInstance && familyInstance.MEPModel != null)
                    {
                        var connectors = familyInstance.MEPModel.ConnectorManager.Connectors
                            .OfType<Connector>()
                            .Where(c => c.IsConnected);

                        loadConnectors.AddRange(connectors);
                    }
                }
                return !loadConnectors.Any();
            }

            return false;
        }

        private void ExportToIFC(Document doc, string filePath, StreamWriter writer)
        {
            try
            {
                string ifcFolderPath = "C:\\Users\\scleu\\Downloads\\2024 Summer Research\\R2024";
                Directory.CreateDirectory(ifcFolderPath);

                string ifcFileName = Path.GetFileNameWithoutExtension(filePath) + ".ifc";
                string ifcFilePath = Path.Combine(ifcFolderPath, ifcFileName);

                // Tab 1: General
                IFCExportOptions ifcOptions = new IFCExportOptions
                {
                    FileVersion = IFCVersion.IFC2x3CV2,
                    ExportBaseQuantities = true,
                    SpaceBoundaryLevel = 2 // Level of space boundaries
                };

                // Set exchange requirements
                ifcOptions.AddOption("ExchangeRequirement", "IFC4_Reference_View"); // Example exchange requirement

                // Set file type
                ifcOptions.AddOption("FileType", "IFC2x3"); // Example file type setting

                // Set the phase to export
                PhaseArray phases = doc.Phases;
                Phase phaseToExport = phases.get_Item(phases.Size - 1); // Export the last phase
                ifcOptions.AddOption("ExportingPhase", phaseToExport.Id.IntegerValue.ToString());

                // Space boundaries
                ifcOptions.AddOption("SpaceBoundaries", "none"); // Example setting for space boundaries

                // Additional IFC export options
                ifcOptions.AddOption("SplitWallsAndColumnsByLevel", "false");

                // Tab 2: Additional Content
                ifcOptions.AddOption("ExportElementsVisibleInView", "false"); // Export only elements visible in the view
                ifcOptions.AddOption("ExportRooms", "false"); // Export rooms
                ifcOptions.AddOption("ExportAreas", "false"); // Export areas
                ifcOptions.AddOption("ExportSpaces", "false"); // Export spaces in 3D views
                ifcOptions.AddOption("ExportSteelElements", "true"); // Include steel elements
                ifcOptions.AddOption("Export2DPlanViewElements", "false"); // Export 2D plan view elements

                // Tab 3: Property Sets
                ifcOptions.AddOption("ExportRevitPropertySets", "false");
                ifcOptions.AddOption("ExportIFCCommonPropertySets", "true");
                ifcOptions.AddOption("ExportBaseQuantities", "true");
                ifcOptions.AddOption("ExportMaterialPropertySets", "false"); // Export material property sets
                ifcOptions.AddOption("ExportSchedulesAsPsets", "false");
                ifcOptions.AddOption("ExportOnlySchedulesContainingIFCPsetOrCommonInTitle", "false"); // Export only schedules containing IFC, Pset, or Common in the title
                ifcOptions.AddOption("ExportUserDefinedPsets", "false");
                ifcOptions.AddOption("ExportUserDefinedPsetsFile", "path_to_user_defined_psets_file.json");
                ifcOptions.AddOption("ExportParameterMappingTable", "false"); // Export parameter mapping table
                ifcOptions.AddOption("ExportUserDefinedParameterMappingFile", "path_to_user_defined_parameter_mapping_file.txt");

                // Tab 4: Level of Detail


                // Tab 5: Advanced
                ifcOptions.AddOption("ExportPartsAsBuildingElements", "false");
                ifcOptions.AddOption("AllowMixedSolidModelRepresentation", "false");
                ifcOptions.AddOption("UseActiveViewGeometry", "false");
                ifcOptions.AddOption("UseActiveViewGeometry", "false");
                ifcOptions.AddOption("UseFamilyAndTypeNameForReference", "false"); // Use family and type name for reference
                ifcOptions.AddOption("Use2DRoomBoundariesForRoomVolume", "false"); // Use 2D room boundaries for room volume
                ifcOptions.AddOption("IncludeIFCSiteElevationInTheSiteLocalPlacementOrigin", "false"); // Include IFC site elevation in the site local placement origin
                ifcOptions.AddOption("StoreIFCGUIDInElementParameterAfterExport", "true"); // Store the IFC GUID in an element parameter after export
                ifcOptions.AddOption("ExportBoundingBox", "false");
                ifcOptions.AddOption("Keep Tessellated Geometry As Triangulation", "false");
                ifcOptions.AddOption("UseTypeNameOnlyForIfcTypeName", "false"); // Use type name only for IfcType name
                ifcOptions.AddOption("UseVisibleRevitNameAsIfcEntityName", "false"); // Use visible Revit name as the IfcEntity name

                // Tab 6: Geographic Reference


                using (Transaction exportTrans = new Transaction(doc, "Export IFC"))
                {
                    exportTrans.Start();
                    doc.Export(ifcFolderPath, ifcFileName, ifcOptions);
                    exportTrans.Commit();
                }

                writer.WriteLine($"{DateTime.Now}: IFC export completed for: {ifcFilePath}");
            }
            catch (Exception ex)
            {
                writer.WriteLine($"{DateTime.Now}: An error occurred during IFC export for file {filePath}: {ex.Message}");
                writer.WriteLine(ex.StackTrace);
            }
        }

    }
}