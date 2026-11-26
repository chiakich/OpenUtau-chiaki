# Model dump

`render --dump DIR` writes `DIR/manifest.json` and one `.npz` per model run:

| File | Stage | Model |
| --- | --- | --- |
| `part00_sentence000.dur_linguistic.npz`, `….dur.npz` | Phonemizer, per sentence (notes that touch) | `dsdur` linguistic encoder and duration predictor |
| `part00_phrase000.pitch_linguistic.npz`, `….pitch.npz` | *Load Rendered Pitch*, per phrase; only with `--load-pitch` | `dspitch` |
| `part00_phrase000.variance_linguistic.npz`, `….variance.npz` | Render, per phrase | `dsvariance` |
| `part00_phrase000.acoustic.npz` | Render, per phrase | acoustic model |
| `part00_phrase000.vocoder.npz` | Render, per phrase | vocoder |

Each `.npz` holds the tensors exactly as fed, named `in.<input name>` (`in.tokens`, `in.durations`, `in.f0`, `in.velocity`, `in.gender`, `in.spk_embed`, `in.depth`, `in.steps`, …), and what the model returned, named `out.<output name>` (`out.mel`, `out.waveform`, …). Phrase numbers match `phrases[].index` of the result.

Files of an earlier run in the same folder are not removed: go by the manifest, or use a fresh folder.

`manifest.json` lists `models` (per track, the model file behind each stage) and `entries`:

| Field | Meaning |
| --- | --- |
| `file`, `stage`, `part` | The `.npz`, its stage, and the part. |
| `phrase` | The render phrase, for the render and pitch stages. |
| `sentence`, `tick` | For the phonemizer stages: the sentence's number within the part, and the tick of its first note. |
| `inputs`, `outputs` | Name, `dtype` and `shape` of every tensor. |
| `cached` | The outputs came from the tensor cache; the model did not run. Usual for the phonemizer stages, which the dump records on a second pass; otherwise only with `--cache-dir`. |
| `run_index` | The model's run count before this run, for [replaying](#replaying-a-dump) the random numbers. `null` when cached, and for the phonemizer stages. |
| `runs` | How many times this stage ran for this phrase; the file holds the last run. |

With the same seed, two dumps are byte-identical.

## Replaying a dump

```python
import json, numpy as np, onnxruntime as ort

dump = "dump"
manifest = json.load(open(f"{dump}/manifest.json"))
stage = "acoustic"
session = ort.InferenceSession(manifest["models"][0]["stages"][stage])
entry = next(e for e in manifest["entries"] if e["stage"] == stage and e["phrase"] == 0)
data = np.load(f"{dump}/{entry['file']}")
feeds = {name[3:]: data[name] for name in data.files if name.startswith("in.")}
# OpenUtau feeds these as shape (1,); the models declare scalars.
feeds = {k: v.reshape(()) if k in ("steps", "depth", "speedup") else v for k, v in feeds.items()}
mel = session.run(None, feeds)[0]
```

That reproduces the inputs. To reproduce the outputs too, the random numbers must match: give the model's random nodes (`RandomNormal`, `RandomNormalLike`, `RandomUniform`, `RandomUniformLike`, `Multinomial`, `Bernoulli`) the seeds the CLI gave them, the k-th such node in graph order getting

```python
float(1 + (seed + 7919 * k) % 16777215)
```

as its `seed` attribute, and run the entries of a stage on one session in `run_index` order. Replayed this way with `onnxruntime` 1.19 on the CPU, against a CPU render, every output of every stage came within 1e-4 of the dump. (Patch with `onnx` in a separate process from the one that runs `onnxruntime` if importing both crashes; on this setup, `onnxruntime` 1.19 also needed `graph_optimization_level = ORT_ENABLE_BASIC` for these models.)
