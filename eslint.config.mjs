// Copyright (c) 2026 James Maes
// SPDX-License-Identifier: MIT
// Based on K22 fbfc8d2cf3b5e16ae9750b04cf9de1794f1e7152.
// KofTwentyTwo TypeScript / JavaScript lint configuration (K22-CODE-TS).
// The Kingsrook layout (3-space indent, Allman braces, if(...) with no space, no more than
// one blank line inside code) is enforced by ESLint Stylistic rather than Prettier, which
// cannot express Allman braces. Type-aware strict rules come from typescript-eslint.
// Install: npm i -D eslint @eslint/js typescript-eslint @stylistic/eslint-plugin typescript
import js from "@eslint/js";
import stylistic from "@stylistic/eslint-plugin";
import tseslint from "typescript-eslint";

export default tseslint.config(
   {
      ignores: ["**", "!tools/", "!tools/**", "!eslint.config.mjs"],
   },
   js.configs.recommended,
   tseslint.configs.strictTypeChecked,
   tseslint.configs.stylisticTypeChecked,
   {
      languageOptions: {
         globals: { process: "readonly", console: "readonly", Buffer: "readonly", URL: "readonly", fetch: "readonly", WebSocket: "readonly", setTimeout: "readonly", clearTimeout: "readonly", structuredClone: "readonly", performance: "readonly", AbortController: "readonly", Headers: "readonly", FormData: "readonly", setInterval: "readonly", clearInterval: "readonly" },
         parserOptions: {
            projectService: true,
            tsconfigRootDir: import.meta.dirname,
         },
      },
      plugins: { "@stylistic": stylistic },
      rules: {
         // Kingsrook layout
         "@stylistic/indent": ["error", 3, { SwitchCase: 1 }],
         "@stylistic/brace-style": ["error", "allman", { allowSingleLine: false }],
         "@stylistic/keyword-spacing": ["error", {
            before: true,
            after: true,
            overrides: {
               if: { after: false },
               for: { after: false },
               while: { after: false },
               switch: { after: false },
               catch: { after: false },
            },
         }],
         // 1 blank line inside code; up to 3 so methods can be separated by 3.
         "@stylistic/no-multiple-empty-lines": ["error", { max: 3, maxBOF: 0, maxEOF: 0 }],
         "@stylistic/lines-between-class-members": ["error", "always", { exceptAfterSingleLine: true }],
         "@stylistic/semi": ["error", "always"],
         "@stylistic/quotes": ["error", "double", { avoidEscape: true }],
         "@stylistic/comma-dangle": ["error", "always-multiline"],
         "@stylistic/eol-last": ["error", "always"],
         "@stylistic/no-tabs": "error",
         "@stylistic/no-trailing-spaces": "error",
         "@stylistic/operator-linebreak": ["error", "before"],
         "@stylistic/dot-location": ["error", "property"],
         "@stylistic/max-len": "off",

         // Kingsrook non-layout rules
         "curly": ["error", "all"],
         "eqeqeq": ["error", "always"],
         "no-console": "error",
         "no-warning-comments": ["error", { terms: ["todo", "fixme"], location: "start" }],
         "@typescript-eslint/explicit-function-return-type": "error",
         "@typescript-eslint/naming-convention": ["error",
            { selector: "default", format: ["camelCase"] },
            { selector: "variable", modifiers: ["const", "global"], format: ["camelCase", "UPPER_CASE"] },
            { selector: "typeLike", format: ["PascalCase"] },
            { selector: "enumMember", format: ["PascalCase", "UPPER_CASE"] },
            { selector: "import", format: null },
            { selector: "objectLiteralProperty", format: null },
         ],
      },
   },
   {
      files: ["**/*.js", "**/*.mjs", "**/*.cjs"],
      extends: [tseslint.configs.disableTypeChecked],
      // Checked JavaScript uses JSDoc plus strict tsc; TS-only return syntax is unavailable.
      rules: { "@typescript-eslint/explicit-function-return-type": "off" },
   },
   {
      files: ["tools/**/*.mjs"],
      // These command-line tools intentionally print their results and diagnostics.
      rules: { "no-console": ["error", { allow: ["log", "error"] }] },
   },
);
