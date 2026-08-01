using System;
using System.IO;
using Tesseract;

namespace MicroCapture.Processing;

public class OcrProcessor
{
    private readonly string _tessDataPath;

    public OcrProcessor(string tessDataPath = "tessdata")
    {
        _tessDataPath = tessDataPath;
        if (!Directory.Exists(_tessDataPath))
        {
            throw new DirectoryNotFoundException($"Tesseract data directory not found: {_tessDataPath}");
        }
    }

    /// <summary>
    /// Performs OCR on the specified image and saves the result to a text file.
    /// </summary>
    /// <param name="imagePath">Path to the processed image</param>
    /// <returns>Path to the generated text file</returns>
    public string ProcessImage(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath}");
        }

        string text = string.Empty;

        // Initialize Tesseract Engine for English
        using (var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default))
        {
            using (var img = Pix.LoadFromFile(imagePath))
            {
                using (var page = engine.Process(img))
                {
                    text = page.GetText();
                }
            }
        }

        // Save to sidecar .txt file
        string txtFileName = Path.ChangeExtension(imagePath, ".txt");
        File.WriteAllText(txtFileName, text);

        return txtFileName;
    }
}
