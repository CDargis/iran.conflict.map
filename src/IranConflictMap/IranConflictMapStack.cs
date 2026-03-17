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
using RetentionDays = Amazon.CDK.AWS.Logs.RetentionDays;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.S3.Deployment;
using Amazon.CDK.AWS.SES;
using Amazon.CDK.AWS.SES.Actions;
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
            Timeout      = Duration.Seconds(15),
            MemorySize   = 256,
            LogRetention = RetentionDays.TWO_WEEKS
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
        // ── Report URL Queue (Extract Lambda → Sync Lambda, or manual submit) ────
        var reportQueue = new Queue(this, "ReportQueue", new QueueProps
        {
            QueueName                 = "iran-conflict-map-report.fifo",
            Fifo                      = true,
            ContentBasedDeduplication = true,
            VisibilityTimeout         = Duration.Minutes(6),  // > sync lambda timeout
            RetentionPeriod           = Duration.Days(7)
        });

        var deadLetterQueue = new Queue(this, "DeadLetterQueue", new QueueProps
        {
            QueueName                 = "iran-conflict-map-dlq.fifo",
            Fifo                      = true,
            ContentBasedDeduplication = true,
            RetentionPeriod           = Duration.Days(14),
            VisibilityTimeout         = Duration.Seconds(30)
        });

        var processorQueue = new Queue(this, "ProcessorQueue", new QueueProps
        {
            QueueName                = "iran-conflict-map-processor.fifo",
            Fifo                     = true,
            ContentBasedDeduplication = true,
            VisibilityTimeout        = Duration.Minutes(6),  // > processor lambda timeout
            RetentionPeriod          = Duration.Days(7),
            DeadLetterQueue          = new DeadLetterQueue
            {
                Queue           = deadLetterQueue,
                MaxReceiveCount = 3
            }
        });

        // ── Review Queue (ambiguous items awaiting human judgment) ────────────────
        var reviewQueue = new Queue(this, "ReviewQueue", new QueueProps
        {
            QueueName                 = "iran-conflict-map-review.fifo",
            Fifo                      = true,
            ContentBasedDeduplication = true,
            VisibilityTimeout         = Duration.Seconds(30),
            RetentionPeriod           = Duration.Days(14)
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
                ["DEAD_LETTER_QUEUE_URL"] = deadLetterQueue.QueueUrl,
                ["REVIEW_QUEUE_URL"]      = reviewQueue.QueueUrl
            },
            Timeout      = Duration.Minutes(5),
            MemorySize   = 512,
            LogRetention = RetentionDays.TWO_WEEKS
        });

        processorLambda.AddEventSource(new SqsEventSource(processorQueue, new SqsEventSourceProps
        {
            BatchSize = 1
        }));

        strikesTable.GrantReadWriteData(processorLambda);
        syncsTable.GrantReadWriteData(processorLambda);
        deadLetterQueue.GrantSendMessages(processorLambda);
        reviewQueue.GrantSendMessages(processorLambda);

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
        syncsTable.GrantReadWriteData(apiLambda);   // write needed for review-approval sync records
        reviewQueue.GrantConsumeMessages(apiLambda);
        processorQueue.GrantSendMessages(apiLambda);

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

        // ── Email S3 Bucket ───────────────────────────────────────────────────
        var emailBucket = new Bucket(this, "EmailBucket", new BucketProps
        {
            BucketName        = "iran-conflict-map-email",
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            RemovalPolicy     = RemovalPolicy.RETAIN,
            LifecycleRules    = new[]
            {
                new LifecycleRule
                {
                    Id         = "ExpireProcessedEmails",
                    Prefix     = "processed/",
                    Expiration = Duration.Days(60),
                    Enabled    = true
                },
                new LifecycleRule
                {
                    Id         = "ExpireOtherEmails",
                    Prefix     = "other/",
                    Expiration = Duration.Days(15),
                    Enabled    = true
                }
            }
        });

        // SES needs permission to deliver to this bucket
        emailBucket.AddToResourcePolicy(new Amazon.CDK.AWS.IAM.PolicyStatement(
            new Amazon.CDK.AWS.IAM.PolicyStatementProps
            {
                Principals = new Amazon.CDK.AWS.IAM.IPrincipal[]
                {
                    new Amazon.CDK.AWS.IAM.ServicePrincipal("ses.amazonaws.com")
                },
                Actions    = new[] { "s3:PutObject" },
                Resources  = new[] { emailBucket.ArnForObjects("*") },
                Conditions = new Dictionary<string, object>
                {
                    ["StringEquals"] = new Dictionary<string, string>
                    {
                        ["aws:Referer"] = this.Account
                    }
                }
            }
        ));

        // ── SES Receipt Rule ──────────────────────────────────────────────────
        // NOTE: after first deploy, activate this rule set with:
        //   aws ses set-active-receipt-rule-set --rule-set-name iran-conflict-map --region us-east-1
        var ruleSet = new ReceiptRuleSet(this, "EmailRuleSet", new ReceiptRuleSetProps
        {
            ReceiptRuleSetName = "iran-conflict-map"
        });

        ruleSet.AddRule("SaveToS3", new ReceiptRuleOptions
        {
            Recipients  = new[] { "sync@chrisdargis.com" },
            ScanEnabled = false,
            Actions     = new IReceiptRuleAction[]
            {
                new S3(new S3Props
                {
                    Bucket          = emailBucket,
                    ObjectKeyPrefix = "inbox/"
                })
            }
        });

        // ── Extract Lambda (S3 inbox → URL construction → report queue) ─────────
        var extractLambda = new LambdaFunction(this, "ExtractFunction", new LambdaFunctionProps
        {
            FunctionName = "iran-conflict-map-extract",
            Runtime      = Amazon.CDK.AWS.Lambda.Runtime.DOTNET_8,
            Handler      = "IranConflictMap.Extract::IranConflictMap.Extract.Function::FunctionHandler",
            Code         = Amazon.CDK.AWS.Lambda.Code.FromAsset("src/IranConflictMap.Extract", new Amazon.CDK.AWS.S3.Assets.AssetOptions
            {
                Bundling = new BundlingOptions
                {
                    Image   = Amazon.CDK.AWS.Lambda.Runtime.DOTNET_8.BundlingImage,
                    Command = new[] { "bash", "-c", "dotnet publish -c Release -o /asset-output" }
                }
            }),
            Environment = new Dictionary<string, string>
            {
                ["EMAIL_BUCKET"]       = emailBucket.BucketName,
                ["EMAIL_INBOX_PREFIX"] = "inbox/",
                ["EMAIL_OTHER_PREFIX"] = "other/",
                ["REPORT_QUEUE_URL"]   = reportQueue.QueueUrl
            },
            Timeout      = Duration.Minutes(2),
            MemorySize   = 256,
            LogRetention = RetentionDays.TWO_WEEKS
        });

        emailBucket.GrantReadWrite(extractLambda);
        extractLambda.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions   = ["s3:DeleteObject"],
            Resources = [emailBucket.ArnForObjects("*")]
        }));
        reportQueue.GrantSendMessages(extractLambda);

        // ── Sync Lambda (report queue → fetch → Claude → processor queue) ──────
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
                ["STRIKES_TABLE"]       = strikesTable.TableName,
                ["SYNCS_TABLE"]         = syncsTable.TableName,
                ["SSM_PREFIX"]          = "/iran-conflict-map",
                ["PROCESSOR_QUEUE_URL"] = processorQueue.QueueUrl,
                ["EMAIL_BUCKET"]        = emailBucket.BucketName
            },
            Timeout      = Duration.Minutes(5),
            MemorySize   = 512,
            LogRetention = RetentionDays.TWO_WEEKS
        });

        syncLambda.AddEventSource(new SqsEventSource(reportQueue, new SqsEventSourceProps
        {
            BatchSize = 1
        }));

        strikesTable.GrantReadData(syncLambda);
        syncsTable.GrantReadWriteData(syncLambda);
        processorQueue.GrantSendMessages(syncLambda);
        reviewQueue.GrantSendMessages(syncLambda);   // in case Sync Lambda ever routes to review directly
        emailBucket.GrantReadWrite(syncLambda);
        syncLambda.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions   = ["s3:DeleteObject"],
            Resources = [emailBucket.ArnForObjects("*")]
        }));
        syncLambda.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions   = ["ssm:GetParameters", "ssm:PutParameter"],
            Resources = [$"arn:aws:ssm:{this.Region}:{this.Account}:parameter/iran-conflict-map/*"]
        }));

        // API: invoke extract for manual trigger; send to report queue for submit-url
        extractLambda.GrantInvoke(apiLambda);
        reportQueue.GrantSendMessages(apiLambda);
        apiLambda.AddEnvironment("EXTRACT_FUNCTION_NAME",  extractLambda.FunctionName);
        apiLambda.AddEnvironment("REPORT_QUEUE_URL",       reportQueue.QueueUrl);
        apiLambda.AddEnvironment("REVIEW_QUEUE_URL",       reviewQueue.QueueUrl);
        apiLambda.AddEnvironment("PROCESSOR_QUEUE_URL",    processorQueue.QueueUrl);
        apiLambda.AddEnvironment("SYNCS_TABLE",            syncsTable.TableName);
        apiLambda.AddEnvironment("SSM_PREFIX",             "/iran-conflict-map");

        apiLambda.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions   = ["ssm:GetParameter"],
            Resources = [$"arn:aws:ssm:{this.Region}:{this.Account}:parameter/iran-conflict-map/sync_key"]
        }));

        // ── EventBridge S3 trigger — inbox/ ObjectCreated → extract Lambda ────
        emailBucket.EnableEventBridgeNotification();

        var inboxRule = new Rule(this, "InboxObjectCreated", new RuleProps
        {
            EventPattern = new EventPattern
            {
                Source     = new[] { "aws.s3" },
                DetailType = new[] { "Object Created" },
                Detail     = new Dictionary<string, object>
                {
                    ["bucket"] = new Dictionary<string, object>
                    {
                        ["name"] = new[] { emailBucket.BucketName }
                    },
                    ["object"] = new Dictionary<string, object>
                    {
                        ["key"] = new object[]
                        {
                            new Dictionary<string, string> { ["prefix"] = "inbox/" }
                        }
                    }
                }
            }
        });
        inboxRule.AddTarget(new Amazon.CDK.AWS.Events.Targets.LambdaFunction(extractLambda));

        // ── 3-hour cron — retry stuck inbox emails ────────────────────────────
        var inboxRetryRule = new Rule(this, "InboxRetrySchedule", new RuleProps
        {
            Schedule = Schedule.Rate(Duration.Hours(3))
        });
        inboxRetryRule.AddTarget(new Amazon.CDK.AWS.Events.Targets.LambdaFunction(extractLambda));

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
