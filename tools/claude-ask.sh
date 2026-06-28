#!/usr/bin/env bash
# claude-ask.sh — Delegate a task to Claude Code CLI using your Anthropic subscription
#
# Usage:
#   ./claude-ask.sh "Review this code for bugs"
#   ./claude-ask.sh "Write unit tests for Foo.cs" --tools "Read,Edit,Bash"
#   cat file.cs | ./claude-ask.sh "Review this file"
#   ./claude-ask.sh "What version is in LootPulse.csproj?" --dir "D:/Gemini/PoE2_MarketFilter"
#
# Requirements:
#   - Claude Code CLI installed and authenticated (run `claude` once to log in)
#   - Active Claude subscription (Pro/Team/Enterprise)

set -euo pipefail

PROMPT=""
TOOLS="Read,Bash"
DIR=""
OUTPUT_FORMAT="text"
MODEL=""

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --tools|-t)
            TOOLS="$2"
            shift 2
            ;;
        --dir|-d)
            DIR="$2"
            shift 2
            ;;
        --model|-m)
            MODEL="$2"
            shift 2
            ;;
        --json|-j)
            OUTPUT_FORMAT="json"
            shift
            ;;
        --help|-h)
            sed -n '2,15p' "$0"
            exit 0
            ;;
        *)
            if [[ -z "$PROMPT" ]]; then
                PROMPT="$1"
            else
                PROMPT="$PROMPT $1"
            fi
            shift
            ;;
    esac
done

# Read from stdin if available (pipe support)
if [[ ! -t 0 ]]; then
    STDIN_CONTENT=$(cat)
    if [[ -n "$STDIN_CONTENT" ]]; then
        if [[ -n "$PROMPT" ]]; then
            PROMPT="$PROMPT

--- stdin content ---
$STDIN_CONTENT"
        else
            PROMPT="$STDIN_CONTENT"
        fi
    fi
fi

if [[ -z "$PROMPT" ]]; then
    echo "Error: No prompt provided. Pass a prompt argument or pipe content via stdin." >&2
    exit 1
fi

# Build the command
CMD=(claude -p --output-format "$OUTPUT_FORMAT" --allowedTools "$TOOLS")

if [[ -n "$DIR" ]]; then
    CMD+=(--add-dir "$DIR")
fi

if [[ -n "$MODEL" ]]; then
    CMD+=(--model "$MODEL")
fi

# Execute
echo "$PROMPT" | "${CMD[@]}"
