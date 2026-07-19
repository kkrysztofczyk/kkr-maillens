using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class AttachmentSizeToleranceTests
{
    [TestMethod]
    public void ExactAndApproximateSizesAreAccepted()
    {
        Assert.IsFalse(AttachmentSizeTolerance.IsGrossMismatch(1_000, 1_000));
        // maly zalacznik: obowiazuje podloga 4 KiB
        Assert.IsFalse(AttachmentSizeTolerance.IsGrossMismatch(1_000 + 4 * 1024, 1_000));
        // duzy zalacznik: obowiazuje 1% (narzut magazynu MAPI w PR_ATTACH_SIZE)
        Assert.IsFalse(AttachmentSizeTolerance.IsGrossMismatch(10_000_000 - 100_000, 10_000_000));
        Assert.IsFalse(AttachmentSizeTolerance.IsGrossMismatch(10_000_000 + 100_000, 10_000_000));
    }

    [TestMethod]
    public void GrossMismatchesAreRejected()
    {
        // tolerancja liczona od rozmiaru DEKLAROWANEGO (drugi argument)
        Assert.IsTrue(AttachmentSizeTolerance.IsGrossMismatch(1_000 + 4 * 1024 + 1, 1_000));
        Assert.IsTrue(AttachmentSizeTolerance.IsGrossMismatch(10_000_000 - 100_001, 10_000_000));
        Assert.IsTrue(AttachmentSizeTolerance.IsGrossMismatch(10_000_000 + 100_001, 10_000_000));
    }

    [TestMethod]
    public void MissingDeclaredSizeIsNeverAMismatch()
    {
        Assert.IsFalse(AttachmentSizeTolerance.IsGrossMismatch(123_456, 0));
        Assert.IsFalse(AttachmentSizeTolerance.IsGrossMismatch(123_456, -1));
    }
}
