# Line counter
Counts lines in files in a directory.

```
Usage:
  LineCounter [<path>] [options]

Arguments:
  <path>  Directory to analyze. [default: .]

Options:
  --pattern <pattern>               Search pattern to filter files. [default: **]
  --grouping <Directory|Extension>  Grouping mode. [default: Extension]
  --buffer-length <buffer-length>   Size of read buffer in bytes. [default: 4096]
  --threads <threads>               Number of threads to read files. [default: 1]
  --version                         Show version information
  -?, -h, --help                    Show help and usage information

```