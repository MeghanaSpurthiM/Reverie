# Contributing to Reverie

Thanks for your interest in Reverie. Bug reports, fixes and documentation improvements are all welcome.

## Report a problem

Open an issue with the steps to reproduce it, what you expected, and what happened. Include your operating system and, for the game, which build you used.

## Set up

| Part | You need |
|---|---|
| Server scripts and tests | Python 3.10 and `server/requirements.txt` |
| Prompt expansion | [Ollama](https://ollama.com) with `llama3.1` |
| Full theme generation | See [Generation Server](docs/SERVER.md) |
| Unity client | Unity 6000.0.56f1 |

## Run the tests

```bash
cd server
pip install -r requirements.txt
python smoke_test.py          # unit + HTTP API tests, then a no-GPU bake
```

To test the Unity client, first copy the sample themes from a Windows build into `client/unity/Assets/StreamingAssets/SampleThemes/`. Then open *Window → General → Test Runner* and run the EditMode and PlayMode tests.

The server tests also run automatically on every push.

## Propose a change

1. Create a branch from `main`.
2. Keep each pull request focused on one change.
3. Add or update tests for behavior changes.
4. Update the docs if the change affects how people use Reverie.

## Keep the study reproducible

Reverie is the software behind a published study, so some behavior is deliberately fixed:

- **Kick motion:** `PaddleKicker` matches the study's motion, and tests guard it.
- **Generation settings:** keep the defaults in `bake_theme.py`, `generator.py` and Theme Tools.
- **Local prompts:** prompt expansion must stay on a local Llama.

## Assets and licenses

Only add assets that you have the right to redistribute, and record their license in [docs/THIRD_PARTY.md](docs/THIRD_PARTY.md). Never commit API keys, participant data or paid assets.

By contributing, you agree that your contribution is released under the [MIT License](LICENSE).

## Conduct and security

This project follows the [Contributor Covenant, version 2.1](https://www.contributor-covenant.org/version/2/1/code_of_conduct/). Report unacceptable behavior, or a security problem, by e-mail to the corresponding author at mmaadugundu@albany.edu, not in a public issue.

The server can start long GPU jobs, so anyone who can reach it can use your GPU. Keep its default of listening on `localhost` (or use an SSH tunnel); if it must listen on a network, set `REVERIE_TOKEN` and use a trusted network. Keep keys in Theme Tools → *API keys*, in `api_config.json` (git-ignored) or in environment variables; builds blank every key.

When you report a bug, include your operating system, the build or commit, the headset if any, and the steps to reproduce it.
