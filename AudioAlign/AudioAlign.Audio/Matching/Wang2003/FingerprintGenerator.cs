﻿using AudioAlign.Audio.DataStructures;
using AudioAlign.Audio.Project;
using AudioAlign.Audio.Streams;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace AudioAlign.Audio.Matching.Wang2003 {
    /// <summary>
    /// Generates fingerprints according to what is described in:
    /// - Wang, Avery. "An Industrial Strength Audio Search Algorithm." ISMIR. 2003.
    /// - Kennedy, Lyndon, and Mor Naaman. "Less talk, more rock: automated organization 
    ///   of community-contributed collections of concert videos." Proceedings of the 
    ///   18th international conference on World wide web. ACM, 2009.
    /// </summary>
    public class FingerprintGenerator {

        private int samplingRate = 11025;
        private int windowSize = 512;
        private int hopSize = 256;

        private float spectrumMinThreshold = -200; // dB volume
        private float spectrumTemporalSmoothingCoefficient = 0.05f;

        private int spectrumSmoothingLength = 3; // the width in samples of the FFT spectrum to average over
        private int peaksPerFrame = 3;
        private int peakFanout = 5;

        private int targetZoneDistance = 2; // time distance in frames
        private int targetZoneLength = 30; // time length in frames
        private int targetZoneWidth = 63; // frequency width in FFT bins

        public event EventHandler<FrameProcessedEventArgs> FrameProcessed;
        public event EventHandler<FingerprintHashEventArgs> FingerprintHashesGenerated;

        public FingerprintGenerator() {
            //
        }

        public void Generate(AudioTrack track) {
            IAudioStream audioStream = new ResamplingStream(
                new MonoStream(AudioStreamFactory.FromFileInfoIeee32(track.FileInfo)),
                ResamplingQuality.Medium, samplingRate);

            STFT stft = new STFT(audioStream, windowSize, hopSize, WindowType.Hann);
            int index = 0;
            int indices = stft.WindowCount;
            int processedFrames = 0;

            float[] spectrum = new float[windowSize / 2];
            //float[] smoothedSpectrum = new float[frameBuffer.Length - frameSmoothingLength + 1]; // the smooved frequency spectrum of the current frame
            //var spectrumSmoother = new SimpleMovingAverage(frameSmoothingLength);
            float[] spectrumTemporalAverage = new float[spectrum.Length]; // a running average of each spectrum bin over time
            float[] spectrumResidual = new float[spectrum.Length]; // the difference between the current spectrum and the moving average spectrum

            var peakHistory = new PeakHistory(1 + targetZoneDistance + targetZoneLength, spectrum.Length / 2);
            var peakPairs = new List<PeakPair>(peaksPerFrame * peakFanout); // keep a single instance of the list to avoid instantiation overhead

            while (stft.HasNext()) {
                // Get the FFT spectrum
                stft.ReadFrame(spectrum);

                // Skip frames whose average spectrum volume is below the threshold
                // This skips silent frames (zero samples) that only contain very low noise from the FFT 
                // and that would screw up the temporal spectrum average below for the following frames.
                if (spectrum.Average() < spectrumMinThreshold) {
                    index++;
                    continue;
                }

                // Smooth the frequency spectrum to remove small peaks
                //spectrumSmoother.Clear();
                //for (int i = 0; i < frameBuffer.Length; i++) {
                //    var avg = spectrumSmoother.Add(frameBuffer[i]);
                //    if (i >= spectrumSmoothingLength) {
                //        smoothedSpectrum[i - spectrumSmoothingLength] = avg;
                //    }
                //}

                // Update the temporal moving bin average
                if (processedFrames == 0) {
                    // Init averages on first frame
                    for (int i = 0; i < spectrum.Length; i++) {
                        spectrumTemporalAverage[i] = spectrum[i];
                    }
                }
                else {
                    // Update averages on all subsequent frames
                    for (int i = 0; i < spectrum.Length; i++) {
                        spectrumTemporalAverage[i] = ExponentialMovingAverage.UpdateMovingAverage(
                            spectrumTemporalAverage[i], spectrumTemporalSmoothingCoefficient, spectrum[i]);
                    }
                }

                // Calculate the residual
                // The residual is the difference of the current spectrum to the temporal average spectrum. The higher
                // a bin residual is, the steeper the increase in energy in that peak.
                for (int i = 0; i < spectrum.Length; i++) {
                    spectrumResidual[i] = spectrum[i] - spectrumTemporalAverage[i] - 90f;
                }

                // Find local peaks in the residual
                // The advantage of finding peaks in the residual instead of the spectrum is that spectrum energy is usually
                // concentrated in the low frequencies, resulting in a clustering if the highest peaks in the lows. Getting
                // peaks from the residual distributes the peaks more evenly across the spectrum.
                var peaks = peakHistory.List; // take oldest list,
                peaks.Clear(); // clear it, and
                FindLocalMaxima(spectrumResidual, peaks); // refill with new peaks

                // Pick the largest n peaks
                int numMaxima = Math.Min(peaks.Count, peaksPerFrame);
                if (numMaxima > 0) {
                    peaks.Sort((p1, p2) => p1.Value == p2.Value ? 0 : p1.Value < p2.Value ? 1 : -1); // order peaks by height
                    if (peaks.Count > numMaxima) {
                        peaks.RemoveRange(numMaxima, peaks.Count - numMaxima); // select the n tallest peaks by deleting the rest
                    }
                    peaks.Sort((p1, p2) => p1.Index == p2.Index ? 0 : p1.Index < p2.Index ? -1 : 1); // sort peaks by index (not really necessary)
                }

                peakHistory.Add(index, peaks);
                
                if (FrameProcessed != null) {
                    // Mark peaks as 0dB for spectrogram display purposes
                    foreach (var peak in peaks) {
                        spectrum[peak.Index] = 0;
                        spectrumResidual[peak.Index] = 0;
                    }

                    FrameProcessed(this, new FrameProcessedEventArgs { 
                        AudioTrack = track, Index = index, Indices = indices,
                        Spectrum = spectrum, SpectrumResidual = spectrumResidual
                    });
                }

                processedFrames++;
                index++;

                if (processedFrames >= peakHistory.Length) {
                    peakPairs.Clear();
                    FindPairs(peakHistory, peakPairs);
                    FireFingerprintHashesGenerated(track, indices, peakPairs);
                }
            }

            // Flush the remaining peaks of the last frames from the history to get all remaining pairs
            for (int i = 0; i < targetZoneLength; i++) {
                var peaks = peakHistory.List;
                peaks.Clear();
                peakHistory.Add(-1, peaks);
                peakPairs.Clear();
                FindPairs(peakHistory, peakPairs);
                FireFingerprintHashesGenerated(track, indices, peakPairs);
            }
        }

        /// <summary>
        /// Local peak picking works as follows: 
        /// A local peak is always a highest value surrounded by lower values. 
        /// In case of a plateu, the index if the first plateu value marks the peak.
        /// 
        ///      |      |    |
        ///      |      |    |
        ///      v      |    |
        ///      ___    |    |
        ///     /   \   v    v
        ///   _/     \       /\_
        ///  /        \_/\  /   \
        /// /             \/     \
        /// </summary>
        private void FindLocalMaxima(float[] data, List<Peak> peakList) {
            float val;
            float lastVal = float.MinValue;
            int anchorIndex = -1;
            for (int i = 0; i < data.Length; i++) {
                val = data[i];

                if (val > lastVal) {
                    // Climbing an increasing slope to a local maximum
                    anchorIndex = i;
                }
                else if (val == lastVal) {
                    // Plateau
                    // anchorIndex stays the same, as the maximum is always at the beginning of a plateau
                }
                else {
                    // Value is decreasing, going down the decreasing slope
                    if (anchorIndex > -1) {
                        // Local maximum found
                        // The first decrease always comes after a peak (or plateau), 
                        // so the last set anchorIndex is the index of the peak.
                        peakList.Add(new Peak(anchorIndex, lastVal));
                        anchorIndex = -1;
                    }
                }

                lastVal = val;
            }

            //Debug.WriteLine("{0} local maxima found", maxima.Count);
        }

        private List<PeakPair> FindPairs(PeakHistory peakHistory, List<PeakPair> peakPairs) {
            var halfWidth = targetZoneWidth / 2;

            // Get pairs from peaks
            // This is a very naive approach that can be improved, e.g. by taking the average peak value into account,
            // which would result in a list of the most prominent peak pairs.
            // For now, this just iterates linearly through frames and their peaks and generates a pair if the
            // constraints of the target area permit, until the max number of pairs has been generated.
            var index = peakHistory.Index;
            foreach (var peak in peakHistory.Lists[0]) {
                int count = 0;
                for (int distance = targetZoneDistance; distance < peakHistory.Length; distance++) {
                    foreach (var targetPeak in peakHistory.Lists[distance]) {
                        if (peak.Index >= targetPeak.Index - halfWidth && peak.Index <= targetPeak.Index + halfWidth) {
                            peakPairs.Add(new PeakPair { Index = index, Peak1 = peak, Peak2 = targetPeak, Distance = distance });
                            if (++count >= peakFanout) {
                                break;
                            }
                        }
                    }
                    if (count >= peakFanout) {
                        break;
                    }
                }
            }

            return peakPairs;
        }

        private void FireFingerprintHashesGenerated(AudioTrack track, int indices, List<PeakPair> peakPairs) {
            if (FingerprintHashesGenerated != null && peakPairs.Count > 0) {
                FingerprintHashesGenerated(this, new FingerprintHashEventArgs {
                    AudioTrack = track,
                    Index = peakPairs[0].Index,
                    Indices = indices,
                    Hashes = peakPairs.ConvertAll(pp => PeakPair.PeakPairToHash(pp))
                });
            }
        }

        [DebuggerDisplay("{index}/{value}")]
        private struct Peak {

            private int index;
            private float value;

            public Peak(int index, float value) {
                this.index = index;
                this.value = value;
            }
            public int Index { get { return index; } }
            public float Value { get { return value; } }
        }

        [DebuggerDisplay("{Index}:{Peak1.Index} --({Distance})--> {Peak2.Index}")]
        private struct PeakPair {
            public int Index { get; set; }
            public Peak Peak1 { get; set; }
            public Peak Peak2 { get; set; }
            public int Distance { get; set; }

            public static FingerprintHash PeakPairToHash(PeakPair pp) {
                // Put frequency bins and the distance each in one byte. The actual quantization
                // is configured through the parameters, e.g. the FFT window size determines the
                // number of frequency bins, and the size of the target zone determines the max
                // distance. Their max size can be anywhere in the range of a byte. if it should be 
                // higher, a quantization step must be introduced (which will basically be a division).
                return new FingerprintHash((uint)((byte)pp.Peak1.Index << 16 | (byte)pp.Peak2.Index << 8 | (byte)pp.Distance));
            }

            public static PeakPair HashToPeakPair(FingerprintHash hash, int index) {
                // The inverse operation of the function above.
                return new PeakPair {
                    Index = index,
                    Peak1 = new Peak((int)(hash.Value >> 16 & 0xFF), 0),
                    Peak2 = new Peak((int)(hash.Value >> 8 & 0xFF), 0),
                    Distance = (int)(hash.Value & 0xFF)
                };
            }
        }

        /// <summary>
        /// Helper class to encapsulate the management of the two FIFOs for building a 
        /// peak history over time which is needed to calculate peak pairs. This history
        /// needs to contain the whole target zone of each peak.
        /// The first history entry, which is the oldest, is always the one for which
        /// pairs are calculated. After calculation, the oldest entries can be taken
        /// through the Index/List properties, updated with new data and readded to the history,
        /// which moves the second oldest entry to the first position (thus, it becomes the oldest),
        /// and the oldest entry gets reused and added as the most recent to the end.
        /// </summary>
        private class PeakHistory {

            private RingBuffer<int> indexHistory; // a FIFO list of peak list indices
            private RingBuffer<List<Peak>> peakHistory; // a FIFO list of peak lists

            public PeakHistory(int length, int maxPeaksPerFrame) {
                indexHistory = new RingBuffer<int>(length);
                peakHistory = new RingBuffer<List<Peak>>(length);

                // Instantiate peak lists for later reuse
                for (int i = 0; i < length; i++) {
                    indexHistory.Add(-1);
                    peakHistory.Add(new List<Peak>(maxPeaksPerFrame));
                }
            }

            /// <summary>
            /// The capacity of the history.
            /// </summary>
            public int Length {
                get { return peakHistory.Length; }
            }

            /// <summary>
            /// The number of elements in the history.
            /// This always equals to the Length because it gets pre-filled
            /// at construction time.
            /// </summary>
            public int Count {
                get { return peakHistory.Count; }
            }

            /// <summary>
            /// The current (oldest) index.
            /// This is the index of the peak list that pairs are calculated for.
            /// </summary>
            public int Index {
                get { return indexHistory[0]; }
            }

            /// <summary>
            /// The current (oldest) peak list. 
            /// This is the peak list that pairs are calculated for.
            /// </summary>
            public List<Peak> List {
                get { return peakHistory[0]; }
            }

            /// <summary>
            /// Gets the FIFO queue of the indices.
            /// </summary>
            public RingBuffer<int> Indices {
                get { return indexHistory; }
            }

            /// <summary>
            /// Gets the FIFO queue of the peak lists.
            /// </summary>
            public RingBuffer<List<Peak>> Lists {
                get { return peakHistory; }
            }

            /// <summary>
            /// Adds an indexed list to the top (most recent position) of the FIFO queue.
            /// </summary>
            /// <param name="index"></param>
            /// <param name="list"></param>
            public void Add(int index, List<Peak> list) {
                indexHistory.Add(index);
                peakHistory.Add(list);
            }
        }
    }
}
