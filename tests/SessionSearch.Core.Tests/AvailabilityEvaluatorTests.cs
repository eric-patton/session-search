using SessionSearch.Core.Models;
using SessionSearch.Core.Sessions;

namespace SessionSearch.Core.Tests;

public sealed class AvailabilityEvaluatorTests
{
    // feat-001/AC-12
    public static TheoryData<AvailabilityInputs, AvailabilityStatus, bool> Cases => new()
    {
        { new(FormatSupported: false), AvailabilityStatus.UnsupportedFormat, false },
        { new(SourcePresent: false), AvailabilityStatus.SourceRemoved, false },
        { new(Archived: true), AvailabilityStatus.Archived, false },
        { new(Active: true), AvailabilityStatus.Active, false },
        { new(PossiblyActive: true), AvailabilityStatus.PossiblyActive, false },
        { new(DirectorySafe: false), AvailabilityStatus.UnsafeDirectory, false },
        { new(DirectoryExists: false), AvailabilityStatus.MissingDirectory, false },
        { new(CliExists: false), AvailabilityStatus.MissingCli, false },
        { new(), AvailabilityStatus.Ready, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Feat001Ac12AppliesStatusPrecedence(
        AvailabilityInputs inputs,
        AvailabilityStatus expected,
        bool expectedCanRun)
    {
        AvailabilityDecision result = AvailabilityEvaluator.Evaluate(inputs);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expectedCanRun, result.CanOpen);
        Assert.Equal(expectedCanRun, result.CanCopy);
        Assert.NotEmpty(result.Reason);
        Assert.NotEmpty(result.SafeAction);
    }

    // feat-001/AC-12
    [Fact]
    public void Feat001Ac12UnsupportedFormatWinsEveryOtherCondition()
    {
        AvailabilityInputs inputs = new(
            FormatSupported: false,
            SourcePresent: false,
            Archived: true,
            Active: true,
            PossiblyActive: true,
            DirectorySafe: false,
            DirectoryExists: false,
            CliExists: false);

        Assert.Equal(
            AvailabilityStatus.UnsupportedFormat,
            AvailabilityEvaluator.Evaluate(inputs).Status);
    }
}
