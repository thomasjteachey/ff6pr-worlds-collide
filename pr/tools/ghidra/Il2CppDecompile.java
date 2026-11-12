// Decompile chosen IL2CPP methods of the PR's GameAssembly.dll, headless.
//
// Args: <script.json> <patterns file> <output dir>
//   script.json   Il2CppDumper's output (method addresses and names, string literals)
//   patterns      one Java regex per line, matched against "Namespace.Class$$Method"
//                 (# comments and blank lines ignored)
//   output dir    one .c file per class with every matched method's decompiled C
//
// No auto-analysis is needed: every method address from script.json is labelled (so calls
// read as names), string literal slots are labelled str_<text>, and only the matched
// methods are turned into functions and decompiled. Field offsets: see dump.cs.
//@category IL2CPP

import com.google.gson.stream.JsonReader;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;
import ghidra.program.model.symbol.SymbolTable;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.regex.Pattern;

public class Il2CppDecompile extends GhidraScript {

    static String clean(String s) {
        String t = s.replaceAll("[^A-Za-z0-9_.$<>]", "_");
        return t.length() > 180 ? t.substring(0, 180) : t;
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        File json = new File(args[0]), patternsFile = new File(args[1]), outDir = new File(args[2]);
        int timeout = args.length > 3 ? Integer.parseInt(args[3]) : 180;  // seconds per function
        outDir.mkdirs();
        List<Pattern> patterns = new ArrayList<>();
        for (String line : java.nio.file.Files.readAllLines(patternsFile.toPath())) {
            line = line.trim();
            if (!line.isEmpty() && !line.startsWith("#")) patterns.add(Pattern.compile(line));
        }
        long base = currentProgram.getImageBase().getOffset();
        SymbolTable st = currentProgram.getSymbolTable();
        Map<Long, String> methods = new LinkedHashMap<>();
        Map<Long, String> signatures = new HashMap<>();
        TreeMap<String, Long> targets = new TreeMap<>();
        int strings = 0;
        try (JsonReader r = new JsonReader(new BufferedReader(new InputStreamReader(new FileInputStream(json), StandardCharsets.UTF_8), 1 << 20))) {
            r.beginObject();
            while (r.hasNext()) {
                String key = r.nextName();
                if (key.equals("ScriptMethod") || key.equals("ScriptString")) {
                    boolean isMethod = key.equals("ScriptMethod");
                    r.beginArray();
                    while (r.hasNext()) {
                        r.beginObject();
                        long a = -1;
                        String name = null, sig = null;
                        while (r.hasNext()) {
                            String f = r.nextName();
                            if (f.equals("Address")) a = r.nextLong();
                            else if (f.equals("Name") || f.equals("Value")) name = r.nextString();
                            else if (f.equals("Signature")) sig = r.nextString();
                            else r.skipValue();
                        }
                        r.endObject();
                        if (a < 0 || name == null) continue;
                        if (isMethod) {
                            if (!methods.containsKey(a)) { methods.put(a, name); signatures.put(a, sig); }
                            for (Pattern p : patterns)
                                if (p.matcher(name).find()) {
                                    // overloads share a name: keep each one (the first under its plain name)
                                    String target = targets.containsKey(name) && targets.get(name) != a
                                        ? name + "#" + Long.toHexString(a) : name;
                                    targets.putIfAbsent(target, a);
                                    break;
                                }
                        } else {
                            String label = "str_" + clean(name.length() > 40 ? name.substring(0, 40) : name);
                            try { st.createLabel(toAddr(base + a), label, SourceType.USER_DEFINED); strings++; } catch (Exception e) { }
                        }
                    }
                    r.endArray();
                } else r.skipValue();
            }
            r.endObject();
        }
        int labelled = 0;
        for (Map.Entry<Long, String> m : methods.entrySet()) {
            try { st.createLabel(toAddr(base + m.getKey()), clean(m.getValue()), SourceType.USER_DEFINED); labelled++; } catch (Exception e) { }
        }
        println("labelled " + labelled + " methods, " + strings + " strings; decompiling " + targets.size());

        DecompInterface ifc = new DecompInterface();
        DecompileOptions opts = new DecompileOptions();
        opts.setMaxPayloadMBytes(512);  // PhysicalExecution overflows the default
        ifc.setOptions(opts);
        ifc.openProgram(currentProgram);
        Map<String, StringBuilder> byClass = new TreeMap<>();
        for (Map.Entry<String, Long> t : targets.entrySet()) {
            Address at = toAddr(base + t.getValue());
            Function f = getFunctionAt(at);
            if (f == null) {
                try { f = createFunction(at, clean(t.getKey())); } catch (Exception e) { f = null; }
            }
            String cls = t.getKey().contains("$$") ? t.getKey().substring(0, t.getKey().indexOf("$$")) : "misc";
            StringBuilder sb = byClass.computeIfAbsent(cls, k -> new StringBuilder());
            sb.append("// ").append(t.getKey()).append("  @ RVA 0x").append(Long.toHexString(t.getValue())).append('\n');
            String sig = signatures.get(t.getValue());
            if (sig != null) sb.append("// ").append(sig).append('\n');
            if (f == null) { sb.append("// (could not create a function here)\n\n"); continue; }
            DecompileResults res = ifc.decompileFunction(f, timeout, monitor);
            if (res != null && res.decompileCompleted()) sb.append(res.getDecompiledFunction().getC()).append('\n');
            else sb.append("// decompile failed: ").append(res == null ? "null" : res.getErrorMessage()).append("\n\n");
        }
        for (Map.Entry<String, StringBuilder> e : byClass.entrySet()) {
            // '<' and '>' (compiler-made classes like Foo.<>c, the lambdas' home) can't be in a
            // Windows file name
            File out = new File(outDir, clean(e.getKey()).replace('<', '(').replace('>', ')') + ".c");
            try (Writer w = new OutputStreamWriter(new FileOutputStream(out), StandardCharsets.UTF_8)) { w.write(e.getValue().toString()); }
        }
        println("wrote " + byClass.size() + " files to " + outDir);
    }
}
