using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Api;
using OpenUtau.Core.Util;

namespace OpenUtau.Core {
    public partial class DocManager {
        // A host without a GUI leaves the user's plugin folder as it is.
        bool readOnlyPluginScan;

        // The app's save tells its user about a failure instead of its caller; this one puts back what the attempt changed.
        internal bool SaveChecked(string path) {
            var project = Project;
            var point = savedPoint;
            bool wasSaved = project.Saved;
            string oldPath = project.FilePath;
            project.Saved = false;
            ExecuteCmd(new SaveProjectNotification(path));
            if (project.Saved) {
                return true;
            }
            project.Saved = wasSaved;
            project.FilePath = oldPath;
            savedPoint = point;
            return false;
        }

        // Initialize without the crash save hook and the live pitch service: a host without a GUI
        // must not write a backup next to the project, nor have pitch loaded behind its back.
        internal void InitializeHeadless(Thread mainThread, TaskScheduler mainScheduler) {
            readOnlyPluginScan = true;
            SearchAllPlugins();
            SearchAllLegacyPlugins();
            this.mainThread = mainThread;
            this.mainScheduler = mainScheduler;
            ThreadGuard.SetUiThread(mainThread);
            PhonemizerRunner = new PhonemizerRunner(mainScheduler);
            new Pipeline.PhraseSourceBuilder(mainScheduler);
        }
    }
}
