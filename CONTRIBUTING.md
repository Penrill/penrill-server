# Contributing to Penrill Server

Thanks for helping! Bugs and ideas are welcome as
[issues](https://github.com/Penrill/penrill-server/issues) in English or Ukrainian. Please include
the server version (`penrill-server --version`), your system and how you installed it (APT, Snap or
`.deb`).

## Working on the code

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
dotnet run --project src/Penrill.Server   # the server on http://127.0.0.1:5081
dotnet test                               # run before every commit
```

The server's web pages live in the main [Penrill](https://github.com/Penrill/penrill) repository
under `apps/server`; see the Development section of the [README](README.md#web-ui-wwwroot).
Code, comments, docs and commit messages are in English.

## License

Penrill Server is released under the [FSL-1.1-MIT](LICENSE) license. Before your first pull request
is merged, please sign the
[Contributor License Agreement](https://github.com/Penrill/penrill/blob/main/CLA.md), the same one
the Penrill editor uses: a bot comments on the pull request and you reply with the sentence it asks
for. You only do this once per repository.

The CLA lets the project owner release your contribution under any license in future versions. You
keep the copyright in your work.
