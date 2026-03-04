using Amazon.CDK;
using Amazon.CDK.AWS.CodeBuild;
using Amazon.CDK.Pipelines;
using Constructs;

namespace IranConflictMap;

public class PipelineStack : Stack
{
    public PipelineStack(Construct scope, string id, IStackProps? props = null)
        : base(scope, id, props)
    {
        const string connectionArn = "arn:aws:codeconnections:us-east-1:853479287330:connection/ee8234da-0a0f-4728-9a3f-236e5b2b8671";
        const string repoOwner    = "CDargis";
        const string repoName     = "iran.conflict.map";
        const string branch       = "main";

        // ── Source ────────────────────────────────────────────────────────
        var source = CodePipelineSource.Connection(
            $"{repoOwner}/{repoName}",
            branch,
            new ConnectionSourceOptions
            {
                ConnectionArn = connectionArn
            }
        );

        // ── Pipeline ──────────────────────────────────────────────────────
        var pipeline = new CodePipeline(this, "Pipeline", new CodePipelineProps
        {
            PipelineName    = "IranConflictMapPipeline",
            SelfMutation    = true,
            CrossAccountKeys = false,

            Synth = new ShellStep("Synth", new ShellStepProps
            {
                Input   = source,
                Commands = new[]
                {
                    "npm install -g aws-cdk",
                    "cd src/IranConflictMap",
                    "dotnet restore",
                    "cdk synth"
                },
                PrimaryOutputDirectory = "cdk.out"
            }),

            CodeBuildDefaults = new CodeBuildOptions
            {
                BuildEnvironment = new BuildEnvironment
                {
                    BuildImage  = LinuxBuildImage.STANDARD_7_0,  // ships with .NET 8
                    ComputeType = ComputeType.SMALL
                }
            }
        });

        // ── Deploy stage ──────────────────────────────────────────────────
        // Adds IranConflictMapStack into the pipeline as a deploy stage.
        // BucketDeployment inside that stack handles syncing frontend/ to S3
        // and invalidating CloudFront automatically on every deploy.
        pipeline.AddStage(new IranConflictMapStage(this, "Deploy", new StageProps
        {
            Env = new Amazon.CDK.Environment
            {
                Account = "853479287330",
                Region  = "us-east-1"
            }
        }));
    }
}

// CDK Pipelines requires the stack to be wrapped in a Stage
public class IranConflictMapStage : Stage
{
    public IranConflictMapStage(Construct scope, string id, StageProps? props = null)
        : base(scope, id, props)
    {
        new IranConflictMapStack(this, "IranConflictMapStack");
    }
}