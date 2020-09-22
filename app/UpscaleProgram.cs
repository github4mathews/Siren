﻿namespace tensorflow.keras {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.Linq;

    using LostTech.Gradient;
    using LostTech.TensorFlow;

    using numpy;

    using tensorflow.core.protobuf.config_pb2;
    using tensorflow.keras.callbacks;
    using tensorflow.keras.layers;
    using tensorflow.keras.losses;
    using tensorflow.keras.optimizers;

    class UpscaleProgram {
        static void Main(string[] args) {
            GradientEngine.UseEnvironmentFromVariable();
            TensorFlowSetup.Instance.EnsureInitialized();

            dynamic sessionConfig = config_pb2.ConfigProto.CreateInstance();
            sessionConfig.gpu_options.allow_growth = true;
            tf.keras.backend.set_session(Session.NewDyn(config: sessionConfig));
            tf.keras.backend.set_floatx("float64");

            // this allows SIREN to oversaturate channels without adding to the loss
            var clampToValidChannelRange = PythonFunctionContainer.Of<Tensor, Tensor>(ClampToValidChannelValueRange);
            var siren = new Sequential(new object[] {
                new GaussianNoise(stddev: 1f/(64*1024)),
                new Siren(2, Enumerable.Repeat(256, 12).ToArray()),
                new Dense(units: 4, activation: clampToValidChannelRange),
                new GaussianNoise(stddev: 1f/64),
            });

            siren.compile(
                // too slow to converge
                optimizer: new SGD(learning_rate: 0.00006),
                // lowered learning rate to avoid destabilization
                //optimizer: new Adam(learning_rate: 0.00033),
                loss: new MeanSquaredError());

            const string saveName = "sample-64.weights";
            const string saveIndexName = saveName + ".index";

            if (args.Length == 0) {
                siren.load_weights("sample.weights");
                Render(siren, 1034*3, 1536*3, "sample6X.png");
                return;
            } else {
                if (System.IO.File.Exists(saveIndexName)) {
                    siren.load_weights(saveName);
                }
            }

            foreach (string imagePath in args) {
                using var original = new Bitmap(imagePath);
                byte[,,] image = ToBytesHWC(original);
                int height = image.GetLength(0);
                int width = image.GetLength(1);
                int channels = image.GetLength(2);
                Debug.Assert(channels == 4);

                var imageSamples = PrepareImage(image);
                var coords = SirenTests.Coord(height, width).ToNumPyArray()
                    .reshape(new[] { width * height, 2 });

                const int cutSize = 200;

                var cut = Enumerable.Range(width*3/4-cutSize/2, cutSize)
                    .SelectMany(x => Enumerable.Range(height*3/4-cutSize/2, cutSize).Select(y => x + y*width))
                    .ToNumPyArray();

                using var _ = Python.Runtime.Py.GIL();
                dynamic numpy = Python.Runtime.Py.Import("numpy");
                imageSamples = numpy.delete(imageSamples, cut, axis: 0);
                coords = numpy.delete(coords, cut, axis: 0);

                var upscaleCoords = SirenTests.Coord(height * 2, width * 2).ToNumPyArray();

                var improved = new ImprovedCallback();
                improved.OnLossImproved += (sender, eventArgs) => {
                    if (eventArgs.Epoch < 1000) return;
                    if (eventArgs.Epoch % 100 != 0 && eventArgs.Epoch < 10000) return;
                    ndarray<float> upscaled = siren.predict(
                        upscaleCoords.reshape(new[] { height * width * 4, 2 }),
                        batch_size: 1024).astype("float32");
                    upscaled = (ndarray<float>)upscaled.reshape(new[] { height * 2, width * 2, channels });
                    using var bitmap = ToImage(RestoreImage(upscaled));
                    bitmap.Save("sample4X.png", ImageFormat.Png);

                    siren.save_weights(saveName);

                    Console.WriteLine();
                    Console.WriteLine("saved!");
                };

                siren.fit(coords, imageSamples, epochs: 400000, batchSize: 4096,
                    shuffleMode: TrainingShuffleMode.Epoch,
                    callbacks: new ICallback[] { improved });
            }
        }

        static void Render(Model siren, int width, int height, string path) {
            var renderCoords = SirenTests.Coord(height, width).ToNumPyArray();
            ndarray<float> renderBytes = siren.predict(
                renderCoords.reshape(new[] { height * width, 2 }),
                batch_size: 1024);
            const int channels = 4;
            renderBytes = (ndarray<float>)renderBytes.reshape(new[] { height, width, channels });
            using var bitmap = ToImage(RestoreImage(renderBytes));
            bitmap.Save(path, ImageFormat.Png);
        }

        class ImprovedCallback : Callback {
            double bestLoss = double.PositiveInfinity;
            public override void on_epoch_end(int epoch, IDictionary<string, dynamic> logs) {
                base.on_epoch_end(epoch, logs);
                if (logs["loss"] < this.bestLoss) {
                    this.bestLoss = logs["loss"];
                    this.OnLossImproved?.Invoke(this, new EpochEndEventArgs {
                        Epoch = epoch,
                        Logs = logs,
                    });
                }
            }

            public event EventHandler<EpochEndEventArgs> OnLossImproved;
        }

        class EpochEndEventArgs : EventArgs {
            public int Epoch { get; set; }
            public IDictionary<string, dynamic> Logs { get; set; }
        }

        static ndarray<float> PrepareImage(byte[,,] image) {
            int height = image.GetLength(0);
            int width = image.GetLength(1);
            int channels = image.GetLength(2);

            var normalized = SirenTests.NormalizeChannelValue(image.ToNumPyArray());
            var flattened = normalized.reshape(new[] { height * width, channels }).astype(np.float32_fn);
            return (ndarray<float>)flattened;
        }

        static Tensor ClampToValidChannelValueRange(Tensor input)
            => tf.clip_by_value(input,
                clip_value_min: SirenTests.NormalizeChannelValue(-0.01f),
                clip_value_max: SirenTests.NormalizeChannelValue(255.01f));

        static unsafe byte[,,] RestoreImage(ndarray<float> learnedImage) {
            (int height, int width, int channels) = (ValueTuple<int, int, int>)learnedImage.shape;
            var bytes = (learnedImage * 128f + 128f).clip(0, 255).astype(np.uint8_fn).tobytes();
            Debug.Assert(bytes.Length == height * width * channels);
            byte[,,] result = new byte[height, width, channels];
            fixed (byte* dest = result)
            fixed (byte* source = bytes)
                Buffer.MemoryCopy(source: source, destination: dest, bytes.Length, bytes.Length);
            return result;
        }

        static unsafe Bitmap ToImage(byte[,,] bytesHWC) {
            if (bytesHWC.GetLength(2) != 4)
                throw new NotSupportedException();
            var bitmap = new Bitmap(bytesHWC.GetLength(1), bytesHWC.GetLength(0));
            int rowBytes = bitmap.Width * 4;
            var rect = new Rectangle(default, new Size(bitmap.Width, bitmap.Height));
            var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try {
                fixed (byte* source = bytesHWC) {
                    for (int y = 0; y < bitmap.Height; y++) {
                        var dest = data.Scan0 + data.Stride * y;
                        Buffer.MemoryCopy(&source[rowBytes * y], destination: (byte*)dest, rowBytes, rowBytes);
                    }
                }
            } finally {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }

        static unsafe byte[,,] ToBytesHWC(Bitmap bitmap) {
            byte[,,] result = new byte[bitmap.Height, bitmap.Width, 4];
            int rowBytes = bitmap.Width * 4;
            var rect = new Rectangle(default, new Size(bitmap.Width, bitmap.Height));
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try {
                fixed (byte* dest = result) {
                    for (int y = 0; y < bitmap.Height; y++) {
                        var source = data.Scan0 + data.Stride * y;
                        Buffer.MemoryCopy((byte*)source, destination: &dest[rowBytes * y], rowBytes, rowBytes);
                    }
                }
            } finally {
                bitmap.UnlockBits(data);
            }

            return result;
        }
    }
}
