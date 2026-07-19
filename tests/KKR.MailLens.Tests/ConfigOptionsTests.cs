namespace KKR.MailLens.Tests;

/// <summary>
/// Walidacja opcji `config`: zla wartosc ma byc twardym bledem (Errors), nie cichym pominieciem,
/// a wartosci poza zakresem (w tym ujemny --max) maja byc odrzucane bez zmiany configu.
/// </summary>
[TestClass]
public sealed class ConfigOptionsTests
{
    [TestMethod]
    public void ValidMax_SetsValueAndMarksChanged()
    {
        var cfg = new AppConfig();
        var result = ConfigOptions.Apply(cfg, ["config", "--max", "250"]);
        Assert.AreEqual(250, cfg.MaxPerFolder);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void MaxZero_AcceptedAsUnlimited()
    {
        var cfg = new AppConfig();
        var result = ConfigOptions.Apply(cfg, ["config", "--max", "0"]);
        Assert.AreEqual(0, cfg.MaxPerFolder);
        Assert.AreEqual(1_000_000, cfg.EffectiveMax);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void NonNumericMax_ReportsErrorAndKeepsOldValue()
    {
        var cfg = new AppConfig { MaxPerFolder = 5000 };
        var result = ConfigOptions.Apply(cfg, ["config", "--max", "abc"]);
        Assert.AreEqual(5000, cfg.MaxPerFolder);
        Assert.IsFalse(result.Changed);
        Assert.AreEqual(1, result.Errors.Count);
        StringAssert.Contains(result.Errors[0], "--max");
        StringAssert.Contains(result.Errors[0], "abc");
    }

    [TestMethod]
    public void NegativeMax_RejectedInsteadOfBecomingUnlimited()
    {
        var cfg = new AppConfig { MaxPerFolder = 5000 };
        var result = ConfigOptions.Apply(cfg, ["config", "--max", "-5"]);
        Assert.AreEqual(5000, cfg.MaxPerFolder);
        Assert.IsFalse(result.Changed);
        Assert.AreEqual(1, result.Errors.Count);
        StringAssert.Contains(result.Errors[0], "--max");
    }

    [TestMethod]
    public void OutOfRangeDpi_ReportsRangeInError()
    {
        var cfg = new AppConfig();
        int previous = cfg.OcrPdfDpi;
        var result = ConfigOptions.Apply(cfg, ["config", "--ocr-pdf-dpi", "5000"]);
        Assert.AreEqual(previous, cfg.OcrPdfDpi);
        Assert.AreEqual(1, result.Errors.Count);
        StringAssert.Contains(result.Errors[0], "72..600");
    }

    [TestMethod]
    public void InvalidBool_ReportsError()
    {
        var cfg = new AppConfig();
        var result = ConfigOptions.Apply(cfg, ["config", "--semantic-enabled", "tak"]);
        Assert.IsFalse(cfg.SemanticEnabled);
        Assert.IsFalse(result.Changed);
        Assert.AreEqual(1, result.Errors.Count);
        StringAssert.Contains(result.Errors[0], "--semantic-enabled");
    }

    [TestMethod]
    public void MinConfidence_ParsesInvariantCulture()
    {
        var cfg = new AppConfig();
        var result = ConfigOptions.Apply(cfg, ["config", "--paddleocr-min-confidence", "0.75"]);
        Assert.AreEqual(0.75, cfg.PaddleOcrMinimumConfidence);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void MinConfidenceAboveOne_Rejected()
    {
        var cfg = new AppConfig();
        double previous = cfg.PaddleOcrMinimumConfidence;
        var result = ConfigOptions.Apply(cfg, ["config", "--paddleocr-min-confidence", "1.5"]);
        Assert.AreEqual(previous, cfg.PaddleOcrMinimumConfidence);
        Assert.AreEqual(1, result.Errors.Count);
    }

    [TestMethod]
    public void MultipleInvalidOptions_CollectAllErrors()
    {
        var cfg = new AppConfig();
        var result = ConfigOptions.Apply(cfg,
            ["config", "--max", "duzo", "--ocr-timeout", "1", "--worker-memory-mb", "xyz"]);
        Assert.AreEqual(3, result.Errors.Count);
    }

    [TestMethod]
    public void MixedValidAndInvalid_ReportsErrorSoCallerDoesNotSave()
    {
        var cfg = new AppConfig();
        var result = ConfigOptions.Apply(cfg, ["config", "--store", "praca", "--max", "abc"]);
        Assert.AreEqual("praca", cfg.StoreFilter); // wartosci poprawne stosujemy, ale...
        Assert.AreEqual(1, result.Errors.Count);   // ...Errors != 0 oznacza: nie zapisywac, kod wyjscia != 0
    }

    [TestMethod]
    public void NoOptions_NoChangeNoErrors()
    {
        var cfg = new AppConfig();
        var result = ConfigOptions.Apply(cfg, ["config"]);
        Assert.IsFalse(result.Changed);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void StringOptions_AreTrimmed()
    {
        var cfg = new AppConfig();
        var result = ConfigOptions.Apply(cfg, ["config", "--tesseract", "  C:\\ocr\\tesseract.exe  "]);
        Assert.AreEqual("C:\\ocr\\tesseract.exe", cfg.TesseractPath);
        Assert.IsTrue(result.Changed);
    }
}
