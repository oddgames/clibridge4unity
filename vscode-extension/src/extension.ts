import * as vscode from "vscode";
import * as child_process from "child_process";
import * as fs from "fs";
import * as path from "path";

const COMPILE_IDLE = "$(zap) Unity: Compile";
const COMPILE_BUSY = "$(sync~spin) Compiling…";
const COMPILE_OK = "$(check) Unity: Compile";
const COMPILE_FAIL = "$(error) Unity: Compile";

const STATUS_IDLE = "$(info) Unity: Status";
const STATUS_BUSY = "$(sync~spin) Status…";

const RESULT_LINGER_MS = 3000;
const SHOW_DETAILS = "Show details";
const COMPILE_NOW = "Compile now";

let compileRunning = false;

export function activate(ctx: vscode.ExtensionContext) {
    const channel = vscode.window.createOutputChannel("Unity Bridge");

    const compileItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    compileItem.text = COMPILE_IDLE;
    compileItem.command = "clibridge.compile";
    compileItem.tooltip = "Run clibridge4unity COMPILE";
    compileItem.show();

    const statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 99);
    statusItem.text = STATUS_IDLE;
    statusItem.command = "clibridge.status";
    statusItem.tooltip = "Run clibridge4unity STATUS";
    statusItem.show();

    const compileCmd = vscode.commands.registerCommand("clibridge.compile", () => {
        if (compileRunning) {
            channel.appendLine("[clibridge] COMPILE already in progress — ignoring click.");
            channel.show(true);
            return;
        }
        compileRunning = true;
        compileItem.text = COMPILE_BUSY;
        channel.show(true);
        runBridge(channel, "COMPILE", (code) => {
            compileRunning = false;
            compileItem.text = code === 0 ? COMPILE_OK : COMPILE_FAIL;
            setTimeout(() => { compileItem.text = COMPILE_IDLE; }, RESULT_LINGER_MS);
        });
    });

    const statusCmd = vscode.commands.registerCommand("clibridge.status", () => {
        statusItem.text = STATUS_BUSY;
        runBridge(channel, "STATUS", (code, output) => {
            statusItem.text = STATUS_IDLE;
            showStatusNotification(channel, code, output);
        });
    });

    ctx.subscriptions.push(compileItem, statusItem, compileCmd, statusCmd, channel);
}

function runBridge(
    channel: vscode.OutputChannel,
    cmd: string,
    onClose: (code: number | null, output: string) => void
) {
    const config = vscode.workspace.getConfiguration("clibridge");
    const exe = config.get<string>("executablePath", "clibridge4unity");
    const projectPath = resolveProjectPath(config);

    channel.appendLine("");
    channel.appendLine(projectPath ? `[cwd: ${projectPath}]` : "[cwd: <no Unity project detected>]");
    channel.appendLine(`> ${exe} ${cmd}`);

    let buffered = "";
    let proc: child_process.ChildProcess;
    try {
        proc = child_process.spawn(exe, [cmd], { shell: true, cwd: projectPath });
    } catch (err) {
        const msg = (err as Error).message;
        channel.appendLine(`[clibridge] failed to spawn ${exe}: ${msg}`);
        onClose(-1, "");
        return;
    }

    const onData = (d: Buffer) => {
        const s = d.toString();
        buffered += s;
        channel.append(s);
    };
    proc.stdout?.on("data", onData);
    proc.stderr?.on("data", onData);
    proc.on("error", (err) => {
        channel.appendLine(`[clibridge] process error: ${err.message}`);
    });
    proc.on("close", (code) => {
        channel.appendLine(`[clibridge] exit code ${code ?? "(none)"}`);
        onClose(code, buffered);
    });
}

async function showStatusNotification(
    channel: vscode.OutputChannel,
    exitCode: number | null,
    output: string
) {
    if (exitCode !== 0) {
        const choice = await vscode.window.showErrorMessage(
            `Unity Bridge: STATUS failed (exit ${exitCode}). Is Unity running?`,
            SHOW_DETAILS
        );
        if (choice === SHOW_DETAILS) channel.show(true);
        return;
    }

    const fields = parseStatus(output);
    const compileErrors = toInt(fields.compileErrorCount);
    const consoleErrors = toInt(fields.consoleErrors);
    const consoleWarnings = toInt(fields.consoleWarnings);
    const scene = fields.currentScene || "(none)";
    const version = fields.unityVersion ? `v${fields.unityVersion}` : "";

    if (isTrue(fields.hasCompileErrors) || consoleErrors > 0) {
        const parts: string[] = [];
        if (compileErrors > 0) parts.push(plural(compileErrors, "compile error"));
        if (consoleErrors > 0) parts.push(plural(consoleErrors, "console error"));
        const choice = await vscode.window.showErrorMessage(
            `Unity: ${parts.join(", ")}`,
            SHOW_DETAILS
        );
        if (choice === SHOW_DETAILS) channel.show(true);
        return;
    }

    if (isTrue(fields.isCompiling)) {
        const choice = await vscode.window.showInformationMessage(
            "Unity is compiling…",
            SHOW_DETAILS
        );
        if (choice === SHOW_DETAILS) channel.show(true);
        return;
    }

    if (isTrue(fields.mainThreadBusy)) {
        const choice = await vscode.window.showInformationMessage(
            "Unity main thread is busy",
            SHOW_DETAILS
        );
        if (choice === SHOW_DETAILS) channel.show(true);
        return;
    }

    if (isTrue(fields.compileRecommended)) {
        const reason = fields.compileRecommendation || "Scripts modified";
        const choice = await vscode.window.showWarningMessage(
            `Unity: compile recommended — ${reason}`,
            COMPILE_NOW,
            SHOW_DETAILS
        );
        if (choice === COMPILE_NOW) {
            vscode.commands.executeCommand("clibridge.compile");
        } else if (choice === SHOW_DETAILS) {
            channel.show(true);
        }
        return;
    }

    const warnSuffix = consoleWarnings > 0 ? ` · ${plural(consoleWarnings, "warning")}` : "";
    const playSuffix = isTrue(fields.isPlaying) ? " · playing" : "";
    const versionSuffix = version ? ` · ${version}` : "";
    const choice = await vscode.window.showInformationMessage(
        `Unity OK · scene: ${scene}${playSuffix}${warnSuffix}${versionSuffix}`,
        SHOW_DETAILS
    );
    if (choice === SHOW_DETAILS) channel.show(true);
}

function resolveProjectPath(config: vscode.WorkspaceConfiguration): string | undefined {
    const configured = config.get<string>("projectPath", "").trim();
    if (configured.length > 0) return configured;

    for (const folder of vscode.workspace.workspaceFolders ?? []) {
        const root = folder.uri.fsPath;
        if (isUnityProject(root)) return root;
    }
    return undefined;
}

function isUnityProject(dir: string): boolean {
    try {
        return fs.existsSync(path.join(dir, "ProjectSettings", "ProjectVersion.txt"));
    } catch {
        return false;
    }
}

function parseStatus(output: string): Record<string, string> {
    const fields: Record<string, string> = {};
    for (const line of output.split(/\r?\n/)) {
        const m = /^([A-Za-z][A-Za-z0-9_]*):\s*(.*)$/.exec(line);
        if (m) fields[m[1]] = m[2].trim();
    }
    return fields;
}

function isTrue(v: string | undefined): boolean {
    return v?.toLowerCase() === "true";
}

function toInt(v: string | undefined): number {
    const n = parseInt(v ?? "0", 10);
    return Number.isFinite(n) ? n : 0;
}

function plural(n: number, noun: string): string {
    return `${n} ${noun}${n === 1 ? "" : "s"}`;
}

export function deactivate() {}
