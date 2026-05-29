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
const UPDATE_EXT = "Update extension";

type CompatState = "compatible" | "incompatible" | "unknown";

let compileItem: vscode.StatusBarItem;
let statusItem: vscode.StatusBarItem;
let versionItem: vscode.StatusBarItem;
let extVersion = "0.0.0";
let compileRunning = false;

export function activate(ctx: vscode.ExtensionContext) {
    const channel = vscode.window.createOutputChannel("Unity Bridge");
    extVersion = (ctx.extension.packageJSON.version as string) ?? "0.0.0";

    compileItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    compileItem.text = COMPILE_IDLE;
    compileItem.command = "clibridge.compile";
    compileItem.tooltip = "Run clibridge4unity COMPILE";
    compileItem.show();

    statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 99);
    statusItem.text = STATUS_IDLE;
    statusItem.command = "clibridge.status";
    statusItem.tooltip = "Run clibridge4unity STATUS";
    statusItem.show();

    // Hidden until a confirmed version mismatch is detected. When shown, it replaces the
    // Compile/Status buttons (fail-closed) and offers a one-click self-update.
    versionItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 98);
    versionItem.command = "clibridge.updateExtension";
    versionItem.hide();

    const compileCmd = vscode.commands.registerCommand("clibridge.compile", async () => {
        if (compileRunning) {
            channel.appendLine("[clibridge] COMPILE already in progress — ignoring click.");
            channel.show(true);
            return;
        }
        if (await guardCompat(channel)) return;
        compileRunning = true;
        compileItem.text = COMPILE_BUSY;
        channel.show(true);
        runBridge(channel, "COMPILE", (code) => {
            compileRunning = false;
            compileItem.text = code === 0 ? COMPILE_OK : COMPILE_FAIL;
            setTimeout(() => { compileItem.text = COMPILE_IDLE; }, RESULT_LINGER_MS);
        });
    });

    const statusCmd = vscode.commands.registerCommand("clibridge.status", async () => {
        if (await guardCompat(channel)) return;
        statusItem.text = STATUS_BUSY;
        runBridge(channel, "STATUS", (code, output) => {
            statusItem.text = STATUS_IDLE;
            showStatusNotification(channel, code, output);
        });
    });

    // One-click self-update: install the version-matched .vsix bundled in the CLI, then reload.
    const updateExtCmd = vscode.commands.registerCommand("clibridge.updateExtension", () => {
        channel.show(true);
        runBridge(channel, "VSCODE", async (code) => {
            if (code === 0) {
                const choice = await vscode.window.showInformationMessage(
                    "Installed the matching Unity clibridge extension. Reload the window to apply.",
                    "Reload Window"
                );
                if (choice === "Reload Window") {
                    vscode.commands.executeCommand("workbench.action.reloadWindow");
                }
            } else {
                const choice = await vscode.window.showErrorMessage(
                    `Failed to install the matching extension (exit ${code}).`,
                    SHOW_DETAILS
                );
                if (choice === SHOW_DETAILS) channel.show(true);
            }
        });
    });

    ctx.subscriptions.push(compileItem, statusItem, versionItem, compileCmd, statusCmd, updateExtCmd, channel);

    // Initial compatibility probe (status-bar only, no popup).
    if (vscode.workspace.getConfiguration("clibridge").get<boolean>("versionCheck", true)) {
        void checkCompat();
    }
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

/**
 * Run a clibridge command and resolve with its exit code + buffered output, without streaming to
 * the channel. Used for the silent BRIDGEINFO handshake. Never rejects: a spawn failure / error
 * resolves to { code: -1 }.
 */
function runBridgeCapture(cmd: string): Promise<{ code: number; output: string }> {
    const config = vscode.workspace.getConfiguration("clibridge");
    const exe = config.get<string>("executablePath", "clibridge4unity");
    const projectPath = resolveProjectPath(config);
    return new Promise((resolve) => {
        let out = "";
        let proc: child_process.ChildProcess;
        try {
            proc = child_process.spawn(exe, [cmd], { shell: true, cwd: projectPath });
        } catch {
            resolve({ code: -1, output: "" });
            return;
        }
        proc.stdout?.on("data", (d: Buffer) => { out += d.toString(); });
        proc.stderr?.on("data", (d: Buffer) => { out += d.toString(); });
        proc.on("error", () => resolve({ code: -1, output: "" }));
        proc.on("close", (code) => resolve({ code: code ?? -1, output: out }));
    });
}

/**
 * Ask the bridge (BRIDGEINFO) whether this extension is still compatible, update the status-bar
 * UI, and return the resulting state. BRIDGEINFO needs no Unity main thread, so it answers even
 * while the Editor is compiling — but it still needs Unity running (else: "unknown").
 */
async function checkCompat(): Promise<CompatState> {
    const { code, output } = await runBridgeCapture("BRIDGEINFO");
    if (code !== 0) {
        applyCompatState("unknown", null, null);
        return "unknown";
    }
    const fields = parseStatus(output);
    const floor = fields.minCompatibleExtensionVersion ?? null;
    const bridgeV = fields.bridgeVersion ?? null;
    const state = compareCompat(extVersion, floor);
    applyCompatState(state, bridgeV, floor);
    return state;
}

/**
 * Re-validate compatibility right before a user-triggered action. Returns true if the action
 * should be ABORTED (confirmed incompatible). Compatible/unknown both proceed (never block on
 * uncertainty). No-op pass-through when the version check is disabled.
 */
async function guardCompat(channel: vscode.OutputChannel): Promise<boolean> {
    if (!vscode.workspace.getConfiguration("clibridge").get<boolean>("versionCheck", true)) {
        return false;
    }
    const state = await checkCompat();
    if (state !== "incompatible") return false;
    channel.appendLine(
        `[clibridge] extension v${extVersion} is older than this Unity bridge requires — update before running commands.`
    );
    void vscode.window.showWarningMessage(
        "The Unity clibridge extension is out of date for the running bridge. Update it to continue.",
        UPDATE_EXT
    ).then((choice) => {
        if (choice === UPDATE_EXT) vscode.commands.executeCommand("clibridge.updateExtension");
    });
    return true;
}

/** Apply a compatibility state to the status bar. Incompatible => fail-closed (hide the buttons). */
function applyCompatState(state: CompatState, bridgeV: string | null, floor: string | null) {
    if (state === "incompatible") {
        compileItem.hide();
        statusItem.hide();
        versionItem.text = "$(warning) Unity: update extension";
        versionItem.tooltip =
            `Extension v${extVersion} is older than this Unity bridge requires ` +
            `(needs ≥ v${floor ?? "?"}; bridge is v${bridgeV ?? "?"}).\n` +
            `Commands are disabled until the extension matches. Click to install the matching version.`;
        versionItem.color = new vscode.ThemeColor("statusBarItem.warningForeground");
        versionItem.show();
    } else {
        // compatible or unknown — never hide on uncertainty
        versionItem.hide();
        compileItem.show();
        statusItem.show();
    }
}

/** Compare extension version against the bridge's minimum-compatible floor (X.Y.Z, numeric). */
function compareCompat(extV: string, floor: string | null): CompatState {
    const a = parseSemver(extV);
    const b = parseSemver(floor);
    if (!a || !b) return "unknown";
    for (let i = 0; i < 3; i++) {
        if (a[i] > b[i]) return "compatible";
        if (a[i] < b[i]) return "incompatible";
    }
    return "compatible"; // equal
}

function parseSemver(v: string | null | undefined): [number, number, number] | null {
    if (!v) return null;
    const m = /(\d+)\.(\d+)\.(\d+)/.exec(v);
    return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
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
