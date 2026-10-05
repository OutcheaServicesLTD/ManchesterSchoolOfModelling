using System.Security.Cryptography;

namespace Msm.Portfolio.Web.Services;

/// <summary>
/// A random password meeting the configured complexity rules, shown once to whoever
/// generated it and never stored in a form it can be read back from.
/// </summary>
/// <remarks>
/// Shared, because two people need one: a Super Admin resetting a staff account, and an
/// Admin giving a model their first way in. One implementation means one set of rules
/// about what a generated password looks like.
/// </remarks>
public static class PasswordGenerator
{
    // No I, l, 1, O or 0. A password is read aloud down a telephone or copied off a
    // screen, and those are the characters that get read back wrong.
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%^&*";

    public static string Create(int length = 14)
    {
        var characters = new List<char>
        {
            Pick(Upper), Pick(Lower), Pick(Digits), Pick(Symbols)
        };

        const string all = Upper + Lower + Digits + Symbols;
        while (characters.Count < length)
        {
            characters.Add(Pick(all));
        }

        // Shuffled so the guaranteed character classes are not always in the same
        // positions, which would narrow the search space.
        return new string([.. characters.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))]);
    }

    private static char Pick(string source) =>
        source[RandomNumberGenerator.GetInt32(source.Length)];
}
