using Amazon.CDK;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.S3.Deployment;
using Constructs;

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
        // Stack is already in us-east-1 per Program.cs so this is fine.
        var certificate = new Certificate(this, "Certificate", new CertificateProps
        {
            DomainName = domainName,
            Validation  = CertificateValidation.FromDns(hostedZone)
        });

        // ── S3 Bucket ──────────────────────────────────────────────────────
        var bucket = new Bucket(this, "SiteBucket", new BucketProps
        {
            BucketName          = domainName,
            BlockPublicAccess   = BlockPublicAccess.BLOCK_ALL,  // CloudFront OAC handles access
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
        var distribution = new Distribution(this, "Distribution", new DistributionProps
        {
            DefaultBehavior = new BehaviorOptions
            {
                Origin               = S3BucketOrigin.WithOriginAccessControl(bucket),
                ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                CachePolicy          = CachePolicy.CACHING_OPTIMIZED,
                AllowedMethods       = AllowedMethods.ALLOW_GET_HEAD,
            },
            DefaultRootObject  = "index.html",
            DomainNames        = new[] { domainName },
            Certificate        = certificate,
            ErrorResponses     = new[]
            {
                // SPA fallback — also handles hard refresh on any path
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
            PriceClass = PriceClass.PRICE_CLASS_100  // US/EU/Israel edge nodes
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
            Sources = new[] { Source.Asset(frontendPath) },
            DestinationBucket   = bucket,
            Distribution        = distribution,
            DistributionPaths   = new[] { "/*" }  // invalidate CloudFront on deploy
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
            Description = "CloudFront distribution ID — use for manual cache invalidations"
        });
        new CfnOutput(this, "BucketName", new CfnOutputProps
        {
            Value       = bucket.BucketName,
            Description = "S3 bucket — upload strikes.json here to update data"
        });
    }
}