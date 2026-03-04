using Amazon.CDK;

namespace IranConflictMap;

sealed class Program
{
    public static void Main(string[] args)
    {
        var app = new App();

        var env = new Amazon.CDK.Environment
        {
            Account = "853479287330",
            Region  = "us-east-1"
        };

        new PipelineStack(app, "PipelineStack", new StackProps { Env = env });

        app.Synth();
    }
}