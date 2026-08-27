using System.Runtime.Versioning;
using System.Text;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.IntegrationTests.Windows;

public sealed class PrivateClipboardTests
{
    [Fact]
    // feat-001/AC-10
    public async Task Feat001Ac10WritesExactUnicodeTextAndBothDwordZeroPrivacyFormats()
    {
        var native = new FakeClipboardNativeApi();
        var staRunner = new InlineStaThreadRunner();
        var clipboard = new PrivateClipboard(
            native,
            staRunner,
            new NoClipboardDelay(),
            maximumAttempts: 3);
        const string text = "Set-Location 'C:\\résumé'; Write-Output '雪'";

        PrivateClipboardResult result = await clipboard.WriteTextAsync(
            text,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, staRunner.Calls);
        Assert.Equal(Encoding.Unicode.GetBytes(string.Concat(text, '\0')), native.Transferred[13]);
        Assert.Equal(new byte[sizeof(uint)], native.Transferred[native.HistoryFormat]);
        Assert.Equal(new byte[sizeof(uint)], native.Transferred[native.CloudFormat]);
        Assert.Empty(native.FreedHandles);
        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    // feat-001/AC-10
    public async Task Feat001Ac10RetriesClipboardContentionWithinTheConfiguredBound()
    {
        var native = new FakeClipboardNativeApi(openResults: [false, false, true]);
        var delay = new NoClipboardDelay();
        var clipboard = new PrivateClipboard(
            native,
            new InlineStaThreadRunner(),
            delay,
            maximumAttempts: 3);

        PrivateClipboardResult result = await clipboard.WriteTextAsync(
            "safe command",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(2, delay.Calls);
        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    // feat-001/AC-10
    public async Task Feat001Ac10ReportsSanitizedFailureAfterClipboardContentionIsExhausted()
    {
        var native = new FakeClipboardNativeApi(openResults: [false, false, false]);
        var clipboard = new PrivateClipboard(
            native,
            new InlineStaThreadRunner(),
            new NoClipboardDelay(),
            maximumAttempts: 3);
        const string secretText = "api_key=never-report-this";

        PrivateClipboardResult result = await clipboard.WriteTextAsync(
            secretText,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(PrivateClipboardFailure.ClipboardBusy, result.Failure);
        Assert.Equal(3, result.Attempts);
        Assert.DoesNotContain(secretText, result.Message, StringComparison.Ordinal);
        Assert.Equal(0, native.CloseCalls);
    }

    [Fact]
    // feat-001/AC-10
    public async Task Feat001Ac10FreesOnlyMemoryWhoseClipboardOwnershipDidNotTransfer()
    {
        var native = new FakeClipboardNativeApi
        {
            FailSetFormat = 13,
        };
        var clipboard = new PrivateClipboard(
            native,
            new InlineStaThreadRunner(),
            new NoClipboardDelay());

        PrivateClipboardResult result = await clipboard.WriteTextAsync(
            "safe command",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(PrivateClipboardFailure.SetClipboardDataFailed, result.Failure);
        Assert.DoesNotContain(new IntPtr(1), native.FreedHandles);
        Assert.DoesNotContain(new IntPtr(2), native.FreedHandles);
        Assert.Contains(new IntPtr(3), native.FreedHandles);
        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    // feat-001/AC-10
    public async Task Feat001Ac10PrivacyFormatFailureOccursBeforeUnicodeTextTransfer()
    {
        var native = new FakeClipboardNativeApi
        {
            FailSetFormat = 100,
        };
        var clipboard = new PrivateClipboard(
            native,
            new InlineStaThreadRunner(),
            new NoClipboardDelay());

        PrivateClipboardResult result = await clipboard.WriteTextAsync(
            "sensitive command",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(native.Transferred.ContainsKey(13));
        Assert.Contains(new IntPtr(1), native.FreedHandles);
    }

    [Fact]
    // feat-001/AC-10
    public async Task Feat001Ac10DoesNotOpenClipboardWhenPrivacyFormatRegistrationFails()
    {
        var native = new FakeClipboardNativeApi
        {
            FailFormatRegistration = true,
        };
        var clipboard = new PrivateClipboard(
            native,
            new InlineStaThreadRunner(),
            new NoClipboardDelay());

        PrivateClipboardResult result = await clipboard.WriteTextAsync(
            "safe command",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(PrivateClipboardFailure.FormatRegistrationFailed, result.Failure);
        Assert.Equal(0, native.OpenCalls);
    }

    [Fact]
    // feat-001/AC-10
    public async Task Feat001Ac10NativeExceptionMessageAndClipboardTextAreNotReturned()
    {
        var native = new FakeClipboardNativeApi
        {
            EmptyException = new InvalidOperationException("native detail includes TOP-SECRET"),
        };
        var clipboard = new PrivateClipboard(
            native,
            new InlineStaThreadRunner(),
            new NoClipboardDelay());

        PrivateClipboardResult result = await clipboard.WriteTextAsync(
            "TOP-SECRET",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(PrivateClipboardFailure.NativeFailure, result.Failure);
        Assert.DoesNotContain("TOP-SECRET", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("native detail", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    // feat-001/AC-10
    public async Task Feat001Ac10WindowsRunnerExecutesClipboardWorkOnStaThread()
    {
        var runner = new WindowsStaThreadRunner();

        ApartmentState apartmentState = await runner.RunAsync(
            () => Thread.CurrentThread.GetApartmentState(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ApartmentState.STA, apartmentState);
    }

    private sealed class InlineStaThreadRunner : IStaThreadRunner
    {
        public int Calls { get; private set; }

        public Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }
    }

    private sealed class NoClipboardDelay : IClipboardRetryDelay
    {
        public int Calls { get; private set; }

        public void Wait(int failedAttempt, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class FakeClipboardNativeApi : IPrivateClipboardNativeApi
    {
        private readonly Queue<bool> openResults;
        private readonly Dictionary<IntPtr, byte[]> allocated = [];
        private int nextHandle = 1;

        public FakeClipboardNativeApi(IEnumerable<bool>? openResults = null)
        {
            this.openResults = new Queue<bool>(openResults ?? [true]);
        }

        public uint HistoryFormat { get; } = 100;

        public uint CloudFormat { get; } = 101;

        public bool FailFormatRegistration { get; set; }

        public uint? FailSetFormat { get; set; }

        public Exception? EmptyException { get; set; }

        public int OpenCalls { get; private set; }

        public int CloseCalls { get; private set; }

        public Dictionary<uint, byte[]> Transferred { get; } = [];

        public List<IntPtr> FreedHandles { get; } = [];

        public uint RegisterClipboardFormat(string formatName)
        {
            if (FailFormatRegistration)
            {
                return 0;
            }

            return formatName switch
            {
                PrivateClipboard.ClipboardHistoryFormatName => HistoryFormat,
                PrivateClipboard.CloudClipboardFormatName => CloudFormat,
                _ => 0,
            };
        }

        public bool OpenClipboard(IntPtr ownerWindow)
        {
            OpenCalls++;
            return openResults.Count > 1 ? openResults.Dequeue() : openResults.Peek();
        }

        public bool EmptyClipboard()
        {
            if (EmptyException is not null)
            {
                throw EmptyException;
            }

            return true;
        }

        public IntPtr AllocateGlobalMemory(int byteCount)
        {
            var handle = new IntPtr(nextHandle++);
            allocated.Add(handle, new byte[byteCount]);
            return handle;
        }

        public void WriteGlobalMemory(IntPtr memory, byte[] bytes)
        {
            allocated[memory] = bytes.ToArray();
        }

        public bool SetClipboardData(uint format, IntPtr memory)
        {
            if (FailSetFormat == format)
            {
                return false;
            }

            Transferred.Add(format, allocated[memory]);
            return true;
        }

        public void FreeGlobalMemory(IntPtr memory)
        {
            FreedHandles.Add(memory);
            allocated.Remove(memory);
        }

        public bool CloseClipboard()
        {
            CloseCalls++;
            return true;
        }
    }
}
