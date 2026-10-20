using System.Security.Cryptography;

using RPM.Desktop.Services.Updates;

// Signs one channel's update feed. Run by both release paths as:
//
//     aws secretsmanager get-secret-value --secret-id RPM/UpdateFeedKey --query SecretString --output text \
//       | dotnet run -c Release --project tools/FeedSign -- <channel> <feed> <output folder>
//
// Reads the private key, PKCS#8 PEM, from standard input. Writes FeedSignature.FileName(channel) into
// the output folder, after checking that the file opens with the public key compiled into the app: a
// secret holding a different key stops the release here, not in every installed copy.

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: FeedSign <channel> <releases.<channel>.json> <output folder>");
    return 2;
}

var (channel, feedPath, outDir) = (args[0], args[1], args[2]);

var pem = Console.In.ReadToEnd();
if (string.IsNullOrWhiteSpace(pem))
{
    Console.Error.WriteLine("FeedSign: no private key on standard input.");
    return 1;
}

using var key = ECDsa.Create();
try
{
    key.ImportFromPem(pem);
}
catch (Exception ex) when (ex is ArgumentException or CryptographicException)
{
    Console.Error.WriteLine("FeedSign: standard input is not a PEM private key.");
    return 1;
}

var feed = File.ReadAllBytes(feedPath);
var signed = FeedSignature.Sign(channel, feed, key);

try
{
    FeedSignature.Open(signed, channel, Convert.FromBase64String(FeedSignature.PublicKey));
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine($"FeedSign: the key on standard input is not the one the app checks with. {ex.Message}");
    return 1;
}

var target = Path.Combine(outDir, FeedSignature.FileName(channel));
File.WriteAllText(target, signed);
Console.WriteLine($"signed {Path.GetFileName(target)} ({feed.Length:N0} bytes of feed)");
return 0;
