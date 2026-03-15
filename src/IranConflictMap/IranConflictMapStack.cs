using Amazon.CDK;
using Amazon.CDK.AWS.Apigatewayv2.Alpha;
using Amazon.CDK.AWS.Apigatewayv2.Integrations.Alpha;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Events;
using Amazon.CDK.AWS.Events.Targets;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda.EventSources;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.S3.Deployment;
using Amazon.CDK.AWS.SQS;
using Amazon.CDK.AWS.SSM;
using Constructs;
using LambdaFunction = Amazon.CDK.AWS.Lambda.Function;
using LambdaFunctionProps = Amazon.CDK.AWS.Lambda.FunctionProps;

namespace IranConflictMap;

public class IranConflictMapStack : Stack
{
    public IranConflictMapStack(Construct scope, string id, IStackProps? props = null)
        : base(scope, id, props)
    {
        const string domainName = "conflictmap.chrisdargis.com";
        const string hostedZoneDomain = "chrisdargis.com";

        // ── Route 53 hosted zone (must already exist) ──────────────────────
        var hostedZone = HostedZone.FromLookup(this, "HostedZone", new HostedZoneProviderProps
        {
            DomainName = hostedZoneDomain
        });

        // ── ACM Certificate (must be us-east-1 for CloudFront) ─────────────
        var certificate = new Certificate(this, "Certificate", new CertificateProps
        {
            DomainName = domainName,
            Validation  = CertificateValidation.FromDns(hostedZone)
        });

        // ── DynamoDB Table ─────────────────────────────────────────────────
        var strikesTable = new Table(this, "StrikesTable", new TableProps
        {
            TableName      = "strikes",
            PartitionKey   = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "id", Type = AttributeType.STRING },
            BillingMode    = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy  = RemovalPolicy.RETAIN
        });

        strikesTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName      = "entity-date-index",
            PartitionKey   = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "entity", Type = AttributeType.STRING },
            SortKey        = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "date",   Type = AttributeType.STRING },
            ProjectionType = ProjectionType.ALL
        });

        // ── API Lambda ─────────────────────────────────────────────────────
        var apiLambda = new LambdaFunction(this, "ApiFunction", new LambdaFunctionProps
        {
            FunctionName = "iran-conflict-map-api",
            Runtime      = Amazon.CDK.AWS.Lambda.Runtime.DOTNET_8,
            Handler      = "IranConflictMap.Api",
            Code         = Amazon.CDK.AWS.Lambda.Code.FromAsset("src/IranConflictMap.Api", new Amazon.CDK.AWS.S3.Assets.AssetOptions
            {
                Bundling = new BundlingOptions
                {
                    Image   = Amazon.CDK.AWS.Lambda.Runtime.DOTNET_8.BundlingImage,
                    Command = new[]
                    {
                        "bash", "-c",
                        "dotnet publish -c Release -o /asset-output"
                    }
                }
            }),
            Environment = new Dictionary<string, string>
            {
                ["STRIKES_TABLE"] = strikesTable.TableName,
                ["STRIKES_GSI"]   = "entity-date-index"
            },
            Timeout    = Duration.Seconds(15),
            MemorySize = 256
        });

        // ── DynamoDB Syncs Table ───────────────────────────────────────────────
        var syncsTable = new Table(this, "SyncsTable", new TableProps
        {
            TableName     = "syncs",
            PartitionKey  = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "id", Type = AttributeType.STRING },
            BillingMode   = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.RETAIN
        });

        syncsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName      = "entity-timestamp-index",
            PartitionKey   = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "entity",    Type = AttributeType.STRING },
            SortKey        = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "timestamp", Type = AttributeType.STRING },
            ProjectionType = ProjectionType.ALL
        });

        // ── SQS Queues ────────────────────────────────────────────────────────
        var deadLetterQueue = new Queue(this, "DeadLetterQueue", new QueueProps
        {
            QueueName         = "iran-conflict-map-dlq",
            RetentionPeriod   = Duration.Days(14),
            VisibilityTimeout = Duration.Seconds(30)
        });

        var processorQueue = new Queue(this, "ProcessorQueue", new QueueProps
        {
            QueueName         = "iran-conflict-map-processor",
            VisibilityTimeout = Duration.Minutes(6),  // > processor lambda timeout
            RetentionPeriod   = Duration.Days(7),
            DeadLetterQueue   = new DeadLetterQueue
            {
                Queue           = deadLetterQueue,
                MaxReceiveCount = 3
            }
        });

        // ── Processor Lambda (SQS-triggered) ─────────────────────────────────
        var processorLambda = new LambdaFunction(this, "ProcessorFunction", new LambdaFunctionProps
        {
            FunctionName = "iran-conflict-map-processor",
            Runtime      = Amazon.CDK.AWS.Lambda.Runtime.DOTNET_8,
            Handler      = "IranConflictMap.Lambda::IranConflictMap.Lambda.Function::FunctionHandler",
            Code         = Amazon.CDK.AWS.Lambda.Code.FromAsset("src/IranConflictMap.Lambda", new Amazon.CDK.AWS.S3.Assets.AssetOptions
            {
                Bundling = new BundlingOptions
                {
                    Image   = Amazon.CDK.AWS.Lambda.Runtime.DOTNET_8.BundlingImage,
                    Command = new[] { "bash", "-c", "dotnet publish -c Release -o /asset-output" }
                }
            }),
            Environment = new Dictionary<string, string>
            {
                ["STRIKES_TABLE"]        = strikesTable.TableName,
                ["SYNCS_TABLE"]          = syncsTable.TableName,
                ["STRIKES_GSI"]          = "entity-date-index",
                ["DEAD_LETTER_QUEUE_URL"] = deadLetterQueue.QueueUrl
            },
            Timeout    = Duration.Minutes(5),
            MemorySize = 512
        });

        processorLambda.AddEventSource(new SqsEventSource(processorQueue, new SqsEventSourceProps
        {
            BatchSize = 1
        }));

        strikesTable.GrantReadWriteData(processorLambda);
        syncsTable.GrantReadWriteData(processorLambda);
        deadLetterQueue.GrantSendMessages(processorLambda);

        // ── SSM Parameters (initial values — updated at runtime by sync Lambda) ──
        new StringParameter(this, "LastSynced", new StringParameterProps
        {
            ParameterName = "/iran-conflict-map/last_synced",
            StringValue   = "2026-02-27"
        });
        new StringParameter(this, "LastRevisionId", new StringParameterProps
        {
            ParameterName = "/iran-conflict-map/last_revision_id",
            StringValue   = "none"
        });

        strikesTable.GrantReadData(apiLambda);
        syncsTable.GrantReadData(apiLambda);

        // ── HTTP API Gateway ───────────────────────────────────────────────
        var httpApi = new HttpApi(this, "HttpApi", new HttpApiProps
        {
            ApiName = "iran-conflict-map-api"
        });

        httpApi.AddRoutes(new AddRoutesOptions
        {
            Path        = "/{proxy+}",
            Methods     = new[] { Amazon.CDK.AWS.Apigatewayv2.Alpha.HttpMethod.ANY },
            Integration = new HttpLambdaIntegration("ApiIntegration", apiLambda)
        });

        // ── Sync Lambda ────────────────────────────────────────────────────────
        var syncLambda = new LambdaFunction(this, "SyncFunction", new LambdaFunctionProps
        {
            FunctionName = "iran-conflict-map-sync",
            Runtime      = Amazon.CDK.AWS.Lambda.Runtime.DOTNET_8,
            Handler      = "IranConflictMap.Sync::IranConflictMap.Sync.Function::FunctionHandler",
            Code         = Amazon.CDK.AWS.Lambda.Code.FromAsset("src/IranConflictMap.Sync", new Amazon.CDK.AWS.S3.Assets.AssetOptions
            {
                Bundling = new BundlingOptions
                {
                    Image   = Amazon.CDK.AWS.Lambda.Runtime.DOTNET_8.BundlingImage,
                    Command = new[] { "bash", "-c", "dotnet publish -c Release -o /asset-output" }
                }
            }),
            Environment = new Dictionary<string, string>
            {
                ["SYNCS_TABLE"]          = syncsTable.TableName,
                ["SSM_PREFIX"]           = "/iran-conflict-map",
                ["PROCESSOR_QUEUE_URL"]  = processorQueue.QueueUrl
            },
            Timeout    = Duration.Minutes(5),
            MemorySize = 512
        });

        syncsTable.GrantReadWriteData(syncLambda);
        processorQueue.GrantSendMessages(syncLambda);

        // SSM: read anthropic_api_key and Graph API creds
        syncLambda.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions   = ["ssm:GetParameters", "ssm:PutParameter"],
            Resources = [$"arn:aws:ssm:{this.Region}:{this.Account}:parameter/iran-conflict-map/*"]
        }));

        // Allow API Lambda to invoke sync Lambda for manual trigger endpoint
        syncLambda.GrantInvoke(apiLambda);
        apiLambda.AddEnvironment("SYNC_FUNCTION_NAME", syncLambda.FunctionName);

        // ── EventBridge Schedule — 9 PM Central (03:00 UTC next day) ─────────
        var syncRule = new Rule(this, "SyncSchedule", new RuleProps
        {
            Schedule = Schedule.Cron(new CronOptions { Hour = "3", Minute = "0" }),
            Enabled  = false
        });
        syncRule.AddTarget(new Amazon.CDK.AWS.Events.Targets.LambdaFunction(syncLambda));

        // ── S3 Bucket ──────────────────────────────────────────────────────
        var bucket = new Bucket(this, "SiteBucket", new BucketProps
        {
            BucketName          = domainName,
            BlockPublicAccess   = BlockPublicAccess.BLOCK_ALL,
            RemovalPolicy       = RemovalPolicy.RETAIN,
            AutoDeleteObjects   = false
        });

        // ── CloudFront Origin Access Control ──────────────────────────────
        var oac = new CfnOriginAccessControl(this, "OAC", new CfnOriginAccessControlProps
        {
            OriginAccessControlConfig = new CfnOriginAccessControl.OriginAccessControlConfigProperty
            {
                Name                          = $"{domainName}-oac",
                OriginAccessControlOriginType = "s3",
                SigningBehavior               = "always",
                SigningProtocol               = "sigv4"
            }
        });

        // ── CloudFront Distribution ────────────────────────────────────────
        var apiOrigin = new HttpOrigin($"{httpApi.HttpApiId}.execute-api.{this.Region}.amazonaws.com");

        var distribution = new Distribution(this, "Distribution", new DistributionProps
        {
            DefaultBehavior = new BehaviorOptions
            {
                Origin               = S3BucketOrigin.WithOriginAccessControl(bucket),
                ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                CachePolicy          = CachePolicy.CACHING_OPTIMIZED,
                AllowedMethods       = AllowedMethods.ALLOW_GET_HEAD,
            },
            AdditionalBehaviors = new Dictionary<string, IBehaviorOptions>
            {
                ["/api/*"] = new BehaviorOptions
                {
                    Origin               = apiOrigin,
                    ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                    CachePolicy          = CachePolicy.CACHING_DISABLED,
                    OriginRequestPolicy  = OriginRequestPolicy.ALL_VIEWER_EXCEPT_HOST_HEADER,
                    AllowedMethods       = AllowedMethods.ALLOW_ALL,
                }
            },
            DefaultRootObject  = "index.html",
            DomainNames        = new[] { domainName },
            Certificate        = certificate,
            ErrorResponses     = new[]
            {
                new ErrorResponse
                {
                    HttpStatus            = 403,
                    ResponseHttpStatus    = 200,
                    ResponsePagePath      = "/index.html",
                    Ttl                   = Duration.Seconds(0)
                },
                new ErrorResponse
                {
                    HttpStatus            = 404,
                    ResponseHttpStatus    = 200,
                    ResponsePagePath      = "/index.html",
                    Ttl                   = Duration.Seconds(0)
                }
            },
            PriceClass = PriceClass.PRICE_CLASS_100
        });

        // Attach OAC to the distribution's S3 origin (L1 escape hatch)
        var cfnDistribution = (CfnDistribution)distribution.Node.DefaultChild!;
        cfnDistribution.AddPropertyOverride(
            "DistributionConfig.Origins.0.OriginAccessControlId",
            oac.AttrId
        );
        cfnDistribution.AddPropertyOverride(
            "DistributionConfig.Origins.0.S3OriginConfig.OriginAccessIdentity",
            ""
        );

        // Grant CloudFront OAC read access to the bucket
        bucket.AddToResourcePolicy(new Amazon.CDK.AWS.IAM.PolicyStatement(
            new Amazon.CDK.AWS.IAM.PolicyStatementProps
            {
                Actions    = new[] { "s3:GetObject" },
                Resources  = new[] { bucket.ArnForObjects("*") },
                Principals = new Amazon.CDK.AWS.IAM.IPrincipal[]
                {
                    new Amazon.CDK.AWS.IAM.ServicePrincipal("cloudfront.amazonaws.com")
                },
                Conditions = new Dictionary<string, object>
                {
                    ["StringEquals"] = new Dictionary<string, string>
                    {
                        ["AWS:SourceArn"] = $"arn:aws:cloudfront::{this.Account}:distribution/{distribution.DistributionId}"
                    }
                }
            }
        ));

        // ── Route 53 A record ──────────────────────────────────────────────
        new ARecord(this, "AliasRecord", new ARecordProps
        {
            Zone       = hostedZone,
            RecordName = domainName,
            Target     = RecordTarget.FromAlias(new CloudFrontTarget(distribution))
        });

        // ── Deploy frontend/ to S3 ─────────────────────────────────────────
        var frontendPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(IranConflictMapStack).Assembly.Location)!,
            "..", "..", "..", // out of bin/Debug/net8.0
            "..", "..",       // out of src/IranConflictMap
            "frontend"
        ));

        new BucketDeployment(this, "DeployFrontend", new BucketDeploymentProps
        {
            Sources             = new[] { Source.Asset(frontendPath) },
            DestinationBucket   = bucket,
            Distribution        = distribution,
            DistributionPaths   = new[] { "/*" },
            CacheControl        = new[] { CacheControl.NoCache() }
        });

        // ── Outputs ────────────────────────────────────────────────────────
        new CfnOutput(this, "SiteUrl", new CfnOutputProps
        {
            Value       = $"https://{domainName}",
            Description = "Conflict map URL"
        });
        new CfnOutput(this, "DistributionId", new CfnOutputProps
        {
            Value       = distribution.DistributionId,
            Description = "CloudFront distribution ID"
        });
        new CfnOutput(this, "BucketName", new CfnOutputProps
        {
            Value       = bucket.BucketName,
            Description = "S3 bucket name"
        });
        new CfnOutput(this, "ApiEndpoint", new CfnOutputProps
        {
            Value       = httpApi.ApiEndpoint,
            Description = "API Gateway endpoint (use via CloudFront /api/*)"
        });
        new CfnOutput(this, "StrikesTableName", new CfnOutputProps
        {
            Value       = strikesTable.TableName,
            Description = "DynamoDB strikes table"
        });
        new CfnOutput(this, "ProcessorQueueUrl", new CfnOutputProps
        {
            Value       = processorQueue.QueueUrl,
            Description = "SQS queue URL for processor Lambda (send seed data here)"
        });
        new CfnOutput(this, "DeadLetterQueueUrl", new CfnOutputProps
        {
            Value       = deadLetterQueue.QueueUrl,
            Description = "SQS dead-letter queue URL"
        });
    }
}
