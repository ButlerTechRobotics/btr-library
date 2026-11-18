using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;

[assembly: ComVisible(false)]

namespace BtrLibrary
{
    /// <summary>
    /// Entry point SOLIDWORKS loads. Registers itself under the SOLIDWORKS add-ins key
    /// when RegAsm runs, and creates the BTR Library task pane on connect.
    /// </summary>
    [ComVisible(true)]
    [Guid("a7b0a297-d259-463f-8c87-ea2df1821fec")]
    [ProgId("BtrLibrary.Addin")]
    public sealed class SwAddin : ISwAddin
    {
        private const string Title = "BTR Library";
        private const string Description = "Browse, search and insert FRC COTS parts (FRCDesignLib-style).";

        private ISldWorks _app;
        private TaskpaneView _view;
        private LibraryPane _pane;

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            _app = (ISldWorks)ThisSW;
            _app.SetAddinCallbackInfo2(0, this, Cookie);

            try
            {
                _view = _app.CreateTaskpaneView3(Icons.EnsureTaskpaneIcons(), Title);
                _pane = new LibraryPane(_app);
                _pane.ReShow = () => _view?.ShowView();
                _pane.CreateControl();
                _view.DisplayWindowFromHandlex64(_pane.Handle.ToInt64());
            }
            catch (Exception ex)
            {
                MessageBox.Show("BTR Library failed to start:\n\n" + ex, Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return true;
        }

        public bool DisconnectFromSW()
        {
            try { _view?.DeleteView(); } catch { /* SOLIDWORKS is shutting down */ }
            _pane?.Dispose();
            if (_view != null) Marshal.ReleaseComObject(_view);
            _view = null;
            _pane = null;
            _app = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        // ---- COM registration (runs during "RegAsm /codebase") ----

        [ComRegisterFunction]
        public static void Register(Type t)
        {
            string guid = t.GUID.ToString("B");
            using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + guid))
            {
                key.SetValue(null, 0);
                key.SetValue("Title", Title);
                key.SetValue("Description", Description);
            }
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + guid))
            {
                key.SetValue(null, 1); // load at startup
            }
        }

        [ComUnregisterFunction]
        public static void Unregister(Type t)
        {
            string guid = t.GUID.ToString("B");
            try { Registry.LocalMachine.DeleteSubKey(@"SOFTWARE\SolidWorks\Addins\" + guid, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKey(@"Software\SolidWorks\AddInsStartup\" + guid, false); } catch { }
        }
    }
}
