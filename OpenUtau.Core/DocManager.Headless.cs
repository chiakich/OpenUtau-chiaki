using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Api;
using OpenUtau.Core.Util;

namespace OpenUtau.Core {
    public partial class DocManager {
        // Initialize without the crash save hook and the live pitch service: a host without a GUI
        // must not write a backup next to the project, nor have pitch loaded behind its back.
        internal void InitializeHeadless(Thread mainThread, TaskScheduler mainScheduler) {
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
