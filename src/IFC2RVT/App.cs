using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace IFC2RVT
{
    /// <summary>Ribbon entry point.</summary>
    public class App : IExternalApplication
    {
        const string TabName = "IFC2RVT";
        const string PanelName = "Конвертация";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Tab already exists - another add-in or a previous load created it.
            }

            var panel = application.CreateRibbonPanel(TabName, PanelName);
            var assembly = Assembly.GetExecutingAssembly().Location;

            var button = new PushButtonData(
                "IFC2RVT_Convert",
                "IFC →\nнативные",
                assembly,
                "IFC2RVT.Commands.ConvertIfcCommand")
            {
                ToolTip = "Создать нативные элементы Revit по данным IFC",
                LongDescription =
                    "Читает IFC напрямую и создаёт стены, перекрытия, колонны, двери, окна и помещения " +
                    "как редактируемые элементы Revit, перенося наборы свойств в общие параметры. " +
                    "Всё, что не имеет однозначной семантики, создаётся как DirectShape."
            };

            panel.AddItem(button);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }
}
