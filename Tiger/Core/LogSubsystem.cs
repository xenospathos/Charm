using Arithmic;
// using MessageBox = System.Windows.Forms.MessageBox;

namespace Tiger;

public class LogSubsystem : Subsystem
{
    protected internal override bool Initialise()
    {
        Log.AddSink<FileSink>();
        Log.AddSink<ConsoleSink>();
        return true;
    }
}
