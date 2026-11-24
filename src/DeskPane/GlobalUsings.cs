// Global usings must not cover module-boundary namespaces (DeskPane.Platform,
// DeskPane.FileSafety, DeskPane.Features, DeskPane.Sync): a global import would
// let a file reach the domain without the namespace string appearing in its
// source, silently bypassing the ratchet's reference checks. Every Platform
// call site carries its own explicit `using DeskPane.Platform;` — that string
// in the file is exactly what the boundary tests enforce on.
