using NAudio.Dsp;

namespace virtual_call_center.Services;

public class DTMFDetector
{
    private readonly ILogger<DTMFDetector> _logger;
    private readonly Dictionary<int, (double freq1, double freq2)> _dtmfFrequencies;
    private readonly int _sampleRate = 8000;
    private readonly int _windowSize = 256; // Power of 2 for FFT
    private readonly double _threshold = 0.3;

    public DTMFDetector(ILogger<DTMFDetector> logger)
    {
        _logger = logger;
        
        _dtmfFrequencies = new Dictionary<int, (double, double)>
        {
            { 1, (697, 1209) }, { 2, (697, 1336) }, { 3, (697, 1477) },
            { 4, (770, 1209) }, { 5, (770, 1336) }, { 6, (770, 1477) },
            { 7, (852, 1209) }, { 8, (852, 1336) }, { 9, (852, 1477) },
            { 0, (941, 1336) }, { 10, (941, 1209) }, { 11, (941, 1477) } // 10 = *, 11 = #
        };
    }

    public int? DetectDTMF(byte[] audioData)
    {
        if (audioData.Length < _windowSize * 2) // 16-bit samples
            return null;

        try
        {
            var samples = ConvertToSamples(audioData);
            var fftData = new Complex[_windowSize];
            
            for (int i = 0; i < _windowSize && i < samples.Length; i++)
            {
                fftData[i] = new Complex { X = samples[i], Y = 0 };
            }

            FastFourierTransform.FFT(true, (int)Math.Log2(_windowSize), fftData);

            foreach (var kvp in _dtmfFrequencies)
            {
                var digit = kvp.Key;
                var (freq1, freq2) = kvp.Value;

                var magnitude1 = GetMagnitudeAtFrequency(fftData, freq1);
                var magnitude2 = GetMagnitudeAtFrequency(fftData, freq2);

                if (magnitude1 > _threshold && magnitude2 > _threshold)
                {
                    _logger.LogDebug("DTMF detected: {Digit} (freqs: {Freq1}Hz={Mag1:F3}, {Freq2}Hz={Mag2:F3})", 
                        digit == 10 ? "*" : digit == 11 ? "#" : digit.ToString(), 
                        freq1, magnitude1, freq2, magnitude2);
                    
                    return digit == 10 ? -1 : digit == 11 ? -2 : digit; // Return -1 for *, -2 for #
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting DTMF");
        }

        return null;
    }

    private float[] ConvertToSamples(byte[] audioData)
    {
        var samples = new float[audioData.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            var sample = BitConverter.ToInt16(audioData, i * 2);
            samples[i] = sample / 32768.0f; // Normalize to -1.0 to 1.0
        }
        return samples;
    }

    private double GetMagnitudeAtFrequency(Complex[] fftData, double frequency)
    {
        var binIndex = (int)Math.Round(frequency * fftData.Length / _sampleRate);
        if (binIndex < 0 || binIndex >= fftData.Length)
            return 0;

        var real = fftData[binIndex].X;
        var imag = fftData[binIndex].Y;
        return Math.Sqrt(real * real + imag * imag) / fftData.Length;
    }
}
