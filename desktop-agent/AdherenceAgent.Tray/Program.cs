using System.Threading;

namespace AdherenceAgent.Tray;

static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    static void Main()
    {
        // Ensure only one instance of the tray app runs
        const string mutexName = "Global\\AdherenceAgentTrayApp";
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        
        if (!createdNew)
        {
            // Another instance is already running
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayAppContext());
        }
        finally
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
    }    
}