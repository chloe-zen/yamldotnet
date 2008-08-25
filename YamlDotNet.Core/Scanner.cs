using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using YamlDotNet.Core.Tokens;

namespace YamlDotNet.Core
{
    /// <summary>
    /// Converts a sequence of characters into a sequence of YAML tokens.
    /// </summary>
    public class Scanner
    {
        private const int MaxVersionNumberLength = 9;

        private Stack<int> indents = new Stack<int>();
        private InsertionQueue<Token> tokens = new InsertionQueue<Token>();
        private Stack<SimpleKey> simpleKeys = new Stack<SimpleKey>();
        private bool streamStartProduced;
        private bool streamEndProduced;
        private int indent = -1;
        private bool simpleKeyAllowed;
        private Mark mark;

        /// <summary>
        /// Gets the current position inside the input stream.
        /// </summary>
        /// <value>The current position.</value>
        public Mark CurrentPosition
        {
            get
            {
                return mark;
            }
        }

        private int flowLevel;
        private int tokensParsed;

        private const int MaxBufferLength = 8;
        private readonly LookAheadBuffer buffer;
        private bool tokenAvailable;

        private static readonly IDictionary<char, char> simpleEscapeCodes = InitializeSimpleEscapeCodes();

        private static IDictionary<char, char> InitializeSimpleEscapeCodes()
        {
            IDictionary<char, char> codes = new SortedDictionary<char, char>();
            codes.Add('0', '\0');
            codes.Add('a', '\x07');
            codes.Add('b', '\x08');
            codes.Add('t', '\x09');
            codes.Add('\t', '\x09');
            codes.Add('n', '\x0A');
            codes.Add('v', '\x0B');
            codes.Add('f', '\x0C');
            codes.Add('r', '\x0D');
            codes.Add('e', '\x1B');
            codes.Add(' ', '\x20');
            codes.Add('"', '"');
            codes.Add('\'', '\'');
            codes.Add('\\', '\\');
            codes.Add('N', '\x85');
            codes.Add('_', '\xA0');
            codes.Add('L', '\x2028');
            codes.Add('P', '\x2029');
            return codes;
        }

        private char ReadCurrentCharacter()
        {
            char currentCharacter = buffer.Peek(0);
            Skip();
            return currentCharacter;
        }

        private char ReadLine()
        {
            if (Check("\r\n\x85")) // CR LF -> LF  --- CR|LF|NEL -> LF
            {
                SkipLine();
                return '\n';
            }

            char nextChar = buffer.Peek(0); // LS|PS -> LS|PS
            SkipLine();
            return nextChar;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Scanner"/> class.
        /// </summary>
        /// <param name="input">The input.</param>
        public Scanner(TextReader input)
        {
            buffer = new LookAheadBuffer(input, MaxBufferLength);
            mark.Column = 0;
            mark.Line = 0;
        }

        private Token current;

        /// <summary>
        /// Gets the current token.
        /// </summary>
        public Token Current
        {
            get
            {
                return current;
            }
        }

        /// <summary>
        /// Moves to the next token.
        /// </summary>
        /// <returns></returns>
        public bool MoveNext()
        {
            if (InternalMoveNext())
            {
                ConsumeCurrent();
                return true;
            }
            else
            {
                return false;
            }
        }

        internal bool InternalMoveNext()
        {
            if (!tokenAvailable && !streamEndProduced)
            {
                FetchMoreTokens();
            }
            if (tokens.Count > 0)
            {
                current = tokens.Dequeue();
                tokenAvailable = false;
                return true;
            }
            else
            {
                current = null;
                return false;
            }
        }

        /// <summary>
        /// Consumes the current token and increments the parsed token count
        /// </summary>
        internal void ConsumeCurrent()
        {
            ++tokensParsed;
            tokenAvailable = false;
            current = null;
            Console.WriteLine("INCREMENT_TOKENS({0})", tokensParsed);
        }

        private void FetchMoreTokens()
        {
            // While we need more tokens to fetch, do it.

            while (true)
            {
                // Check if we really need to fetch more tokens.

                bool needsMoreTokens = false;

                if (tokens.Count == 0)
                {
                    // Queue is empty.

                    needsMoreTokens = true;
                }
                else
                {
                    // Check if any potential simple key may occupy the head position.

                    StaleSimpleKeys();

                    foreach (SimpleKey simpleKey in simpleKeys)
                    {
                        if (simpleKey.IsPossible && simpleKey.TokenNumber == tokensParsed)
                        {
                            needsMoreTokens = true;
                            break;
                        }
                    }
                }

                // We are finished.
                if (!needsMoreTokens)
                {
                    break;
                }

                // Fetch the next token.

                FetchNextToken();
            }
            tokenAvailable = true;
        }

        private static bool StartsWith(StringBuilder what, char start)
        {
            return what.Length > 0 && what[0] == start;
        }

        /// <summary>
        /// Check the list of potential simple keys and remove the positions that
        /// cannot contain simple keys anymore.
        /// </summary>

        private void StaleSimpleKeys()
        {
            // Check for a potential simple key for each flow level.

            foreach (SimpleKey key in simpleKeys)
            {

                // The specification requires that a simple key

                //  - is limited to a single line,
                //  - is shorter than 1024 characters.


                if (key.IsPossible && (key.Mark.Line < mark.Line || key.Mark.Index + 1024 < mark.Index))
                {

                    // Check if the potential simple key to be removed is required.

                    if (key.IsRequired)
                    {
                        throw new SyntaxErrorException("While scanning a simple key, could not found expected ':'.", mark);
                    }

                    key.IsPossible = false;
                }
            }
        }

        private void FetchNextToken()
        {
            Console.WriteLine("Tokens parsed = {0}", tokensParsed);

            // Ensure that the buffer is initialized.

            buffer.Cache(1);

            // Check if we just started scanning.  Fetch STREAM-START then.

            if (!streamStartProduced)
            {
                FetchStreamStart();
                return;
            }

            // Eat whitespaces and comments until we reach the next token.

            ScanToNextToken();

            // Remove obsolete potential simple keys.

            StaleSimpleKeys();

            // Check the indentation level against the current column.

            UnrollIndent(mark.Column);


            // Ensure that the buffer contains at least 4 characters.  4 is the length
            // of the longest indicators ('--- ' and '... ').


            buffer.Cache(4);

            // Is it the end of the stream?

            if (buffer.EndOfInput)
            {
                FetchStreamEnd();
                return;
            }

            // Is it a directive?

            if (mark.Column == 0 && Check('%'))
            {
                FetchDirective();
                return;
            }

            // Is it the document start indicator?

            bool isDocumentStart =
                mark.Column == 0 &&
                Check('-', 0) &&
                Check('-', 1) &&
                Check('-', 2) &&
                IsBlankOrBreakOrZero(3);

            if (isDocumentStart)
            {
                FetchDocumentIndicator(true);
                return;
            }

            // Is it the document end indicator?

            bool isDocumentEnd =
                mark.Column == 0 &&
                Check('.', 0) &&
                Check('.', 1) &&
                Check('.', 2) &&
                IsBlankOrBreakOrZero(3);

            if (isDocumentEnd)
            {
                FetchDocumentIndicator(false);
                return;
            }

            // Is it the flow sequence start indicator?

            if (Check('['))
            {
                FetchFlowCollectionStart(true);
                return;
            }

            // Is it the flow mapping start indicator?

            if (Check('{'))
            {
                FetchFlowCollectionStart(false);
                return;
            }

            // Is it the flow sequence end indicator?

            if (Check(']'))
            {
                FetchFlowCollectionEnd(true);
                return;
            }

            // Is it the flow mapping end indicator?

            if (Check('}'))
            {
                FetchFlowCollectionEnd(false);
                return;
            }

            // Is it the flow entry indicator?

            if (Check(','))
            {
                FetchFlowEntry();
                return;
            }

            // Is it the block entry indicator?

            if (Check('-') && IsBlankOrBreakOrZero(1))
            {
                FetchBlockEntry();
                return;
            }

            // Is it the key indicator?

            if (Check('?') && (flowLevel > 0 || IsBlankOrBreakOrZero(1)))
            {
                FetchKey();
                return;
            }

            // Is it the value indicator?

            if (Check(':') && (flowLevel > 0 || IsBlankOrBreakOrZero(1)))
            {
                FetchValue();
                return;
            }

            // Is it an alias?

            if (Check('*'))
            {
                FetchAnchor(true);
                return;
            }

            // Is it an anchor?

            if (Check('&'))
            {
                FetchAnchor(false);
                return;
            }

            // Is it a tag?

            if (Check('!'))
            {
                FetchTag();
                return;
            }

            // Is it a literal scalar?

            if (Check('|') && flowLevel == 0)
            {
                FetchBlockScalar(true);
                return;
            }

            // Is it a folded scalar?

            if (Check('>') && flowLevel == 0)
            {
                FetchBlockScalar(false);
                return;
            }

            // Is it a single-quoted scalar?

            if (Check('\''))
            {
                FetchFlowScalar(true);
                return;
            }

            // Is it a double-quoted scalar?

            if (Check('"'))
            {
                FetchFlowScalar(false);
                return;
            }


            // Is it a plain scalar?

            // A plain scalar may start with any non-blank characters except

            //      '-', '?', ':', ',', '[', ']', '{', '}',
            //      '#', '&', '*', '!', '|', '>', '\'', '\"',
            //      '%', '@', '`'.

            // In the block context (and, for the '-' indicator, in the flow context
            // too), it may also start with the characters

            //      '-', '?', ':'

            // if it is followed by a non-space character.

            // The last rule is more restrictive than the specification requires.


            bool isInvalidPlainScalarCharacter = IsBlankOrBreakOrZero() || Check("-?:,[]{}#&*!|>'\"%@`");

            bool isPlainScalar =
                !isInvalidPlainScalarCharacter ||
                (Check('-') && !IsBlank(1)) ||
                (flowLevel == 0 && (Check("?:")) && !IsBlankOrBreakOrZero(1));

            if (isPlainScalar)
            {
                FetchPlainScalar();
                return;
            }


            // If we don't determine the token type so far, it is an error.


            throw new SyntaxErrorException("While scanning for the next token, found character that cannot start any token.", mark);
        }

        private bool Check(char expected)
        {
            return Check(expected, 0);
        }

        private bool Check(char expected, int offset)
        {
            return buffer.Peek(offset) == expected;
        }

        private bool Check(string expectedCharacters)
        {
            return Check(expectedCharacters, 0);
        }

        private bool Check(string expectedCharacters, int offset)
        {
            Debug.Assert(expectedCharacters.Length > 1, "Use Check(char, int) instead.");

            char character = buffer.Peek(offset);

            foreach (char expected in expectedCharacters)
            {
                if (expected == character)
                {
                    return true;
                }
            }
            return false;
        }

        private bool CheckWhiteSpace()
        {
            return Check(' ') || ((flowLevel > 0 || !simpleKeyAllowed) && Check('\t'));
        }

        private bool IsDocumentIndicator()
        {
            if (mark.Column == 0 && IsBlankOrBreakOrZero(3))
            {
                bool isDocumentStart = Check('-', 0) && Check('-', 1) && Check('-', 2);
                bool isDocumentEnd = Check('.', 0) && Check('.', 1) && Check('.', 2);

                return isDocumentStart || isDocumentEnd;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Check if the character at the specified position is an alphabetical
        /// character, a digit, '_', or '-'.
        /// </summary>

        private bool IsAlpha(int offset)
        {
            char character = buffer.Peek(offset);

            return
                (character >= '0' && character <= '9') ||
                (character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z') ||
                character == '_' ||
                character == '-';

        }

        private bool IsAlpha()
        {
            return IsAlpha(0);
        }

        /// <summary>
        /// Check if the character at the specified position is a digit.
        /// </summary>

        private bool IsDigit(int offset)
        {
            char character = buffer.Peek(offset);
            return character >= '0' && character <= '9';
        }

        private bool IsDigit()
        {
            return IsDigit(0);
        }

        /// <summary>
        /// Get the value of a digit.
        /// </summary>

        private int AsDigit(int offset)
        {
            return buffer.Peek(offset) - '0';
        }

        private int AsDigit()
        {
            return AsDigit(0);
        }

        /// <summary>
        /// Check if the character at the specified position is a hex-digit.
        /// </summary>

        private bool IsHex(int offset)
        {
            char character = buffer.Peek(offset);
            return
                (character >= '0' && character <= '9') ||
                (character >= 'A' && character <= 'F') ||
                (character >= 'a' && character <= 'f');
        }

        /// <summary>
        /// Get the value of a hex-digit.
        /// </summary>

        private int AsHex(int offset)
        {
            char character = buffer.Peek(offset);

            if (character <= '9')
            {
                return character - '0';
            }
            else if (character <= 'F')
            {
                return character - 'A' + 10;
            }
            else
            {
                return character - 'a' + 10;
            }
        }

        /// <summary>
        /// Check if the character at the specified position is NUL.
        /// </summary>

        private bool IsZero(int offset)
        {
            return Check('\0', offset);
        }

        private bool IsZero()
        {
            return IsZero(0);
        }

        /// <summary>
        /// Check if the character at the specified position is space.
        /// </summary>

        private bool IsSpace(int offset)
        {
            return Check(' ', offset);
        }

        private bool IsSpace()
        {
            return IsSpace(0);
        }

        /// <summary>
        /// Check if the character at the specified position is tab.
        /// </summary>

        private bool IsTab(int offset)
        {
            return Check('\t', offset);
        }

        private bool IsTab()
        {
            return IsTab(0);
        }

        /// <summary>
        /// Check if the character at the specified position is blank (space or tab).
        /// </summary>

        private bool IsBlank(int offset)
        {
            return IsSpace(offset) || IsTab(offset);
        }

        private bool IsBlank()
        {
            return IsBlank(0);
        }

        /// <summary>
        /// Check if the character at the specified position is a line break.
        /// </summary>

        private bool IsBreak(int offset)
        {
            return Check("\r\n\x85\x2028\x2029", offset);
        }

        private bool IsBreak()
        {
            return IsBreak(0);
        }

        private bool IsCrLf(int offset)
        {
            return Check('\r', offset) && Check('\n', offset + 1);
        }

        private bool IsCrLf()
        {
            return IsCrLf(0);
        }

        /// <summary>
        /// Check if the character is a line break or NUL.
        /// </summary>

        private bool IsBreakOrZero(int offset)
        {
            return IsBreak(offset) || IsZero(offset);
        }

        private bool IsBreakOrZero()
        {
            return IsBreakOrZero(0);
        }

        /// <summary>
        /// Check if the character is a line break, space, tab, or NUL.
        /// </summary>

        private bool IsBlankOrBreakOrZero(int offset)
        {
            return IsBlank(offset) || IsBreakOrZero(offset);
        }

        private bool IsBlankOrBreakOrZero()
        {
            return IsBlankOrBreakOrZero(0);
        }

        private void Skip()
        {
            ++mark.Index;
            ++mark.Column;
            buffer.Skip(1);
        }

        private void SkipLine()
        {
            if (IsCrLf())
            {
                mark.Index += 2;
                mark.Column = 0;
                ++mark.Line;
                buffer.Skip(2);
            }
            else if (IsBreak())
            {
                ++mark.Index;
                mark.Column = 0;
                ++mark.Line;
                buffer.Skip(1);
            }
            else if (!IsZero())
            {
                throw new InvalidOperationException("Not at a break.");
            }
        }

        private void ScanToNextToken()
        {
            // Until the next token is not found.

            for (; ; )
            {

                // Eat whitespaces.

                // Tabs are allowed:

                //  - in the flow context;
                //  - in the block context, but not at the beginning of the line or
                //  after '-', '?', or ':' (complex value).  


                buffer.Cache(1);

                while (CheckWhiteSpace())
                {
                    Skip();
                    buffer.Cache(1);
                }

                // Eat a comment until a line break.

                if (Check('#'))
                {
                    while (!IsBreakOrZero())
                    {
                        Skip();
                        buffer.Cache(1);
                    }
                }

                // If it is a line break, eat it.

                if (IsBreak())
                {
                    buffer.Cache(2);
                    SkipLine();

                    // In the block context, a new line may start a simple key.

                    if (flowLevel == 0)
                    {
                        simpleKeyAllowed = true;
                    }
                }
                else
                {
                    // We have found a token.

                    break;
                }
            }
        }

        private void FetchStreamStart()
        {
            // Initialize the simple key stack.

            simpleKeys.Push(new SimpleKey());

            // A simple key is allowed at the beginning of the stream.

            simpleKeyAllowed = true;

            // We have started.

            streamStartProduced = true;

            // Create the STREAM-START token and append it to the queue.

            tokens.Enqueue(new StreamStart(mark, mark));
        }

        /// <summary>
        /// Pop indentation levels from the indents stack until the current level
        /// becomes less or equal to the column.  For each intendation level, append
        /// the BLOCK-END token.
        /// </summary>

        private void UnrollIndent(int column)
        {
            // In the flow context, do nothing.

            if (flowLevel != 0)
            {
                return;
            }

            // Loop through the intendation levels in the stack.

            while (indent > column)
            {
                // Create a token and append it to the queue.

                tokens.Enqueue(new BlockEnd(mark, mark));

                // Pop the indentation level.

                indent = indents.Pop();
            }
        }

        /// <summary>
        /// Produce the STREAM-END token and shut down the scanner.
        /// </summary>
        private void FetchStreamEnd()
        {
            // Force new line.

            if (mark.Column != 0)
            {
                mark.Column = 0;
                ++mark.Line;
            }

            // Reset the indentation level.

            UnrollIndent(-1);

            // Reset simple keys.

            RemoveSimpleKey();

            simpleKeyAllowed = false;

            // Create the STREAM-END token and append it to the queue.

            streamEndProduced = true;
            tokens.Enqueue(new StreamEnd(mark, mark));
        }

        private void FetchDirective()
        {
            // Reset the indentation level.

            UnrollIndent(-1);

            // Reset simple keys.

            RemoveSimpleKey();

            simpleKeyAllowed = false;

            // Create the YAML-DIRECTIVE or TAG-DIRECTIVE token.

            Token token = ScanDirective();

            // Append the token to the queue.

            tokens.Enqueue(token);
        }

        /// <summary>
        /// Scan a YAML-DIRECTIVE or TAG-DIRECTIVE token.
        ///
        /// Scope:
        ///      %YAML    1.1    # a comment \n
        ///      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
        ///      %TAG    !yaml!  tag:yaml.org,2002:  \n
        ///      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
        /// </summary>
        private Token ScanDirective()
        {
            // Eat '%'.

            Mark start = mark;

            Skip();

            // Scan the directive name.

            string name = ScanDirectiveName(start);

            // Is it a YAML directive?

            Token directive;
            switch (name)
            {
                case "YAML":
                    directive = ScanVersionDirectiveValue(start);
                    break;

                case "TAG":
                    directive = ScanTagDirectiveValue(start);
                    break;

                default:
                    throw new SyntaxErrorException("While scanning a directive, found uknown directive name.", start);
            }

            // Eat the rest of the line including any comments.

            buffer.Cache(1);

            while (IsBlank())
            {
                Skip();
                buffer.Cache(1);
            }

            if (Check('#'))
            {
                while (!IsBreakOrZero())
                {
                    Skip();
                    buffer.Cache(1);
                }
            }

            // Check if we are at the end of the line.

            if (!IsBreakOrZero())
            {
                throw new SyntaxErrorException("While scanning a directive, did not found expected comment or line break.", start);
            }

            // Eat a line break.

            if (IsBreak())
            {
                buffer.Cache(2);
                SkipLine();
            }

            return directive;
        }

        /// <summary>
        /// Produce the DOCUMENT-START or DOCUMENT-END token.
        /// </summary>

        private void FetchDocumentIndicator(bool isStartToken)
        {
            // Reset the indentation level.

            UnrollIndent(-1);

            // Reset simple keys.

            RemoveSimpleKey();

            simpleKeyAllowed = false;

            // Consume the token.

            Mark start = mark;

            Skip();
            Skip();
            Skip();

            Token token = isStartToken ? (Token)new DocumentStart(start, mark) : new DocumentEnd(start, start);
            tokens.Enqueue(token);
        }

        /// <summary>
        /// Produce the FLOW-SEQUENCE-START or FLOW-MAPPING-START token.
        /// </summary>

        private void FetchFlowCollectionStart(bool isSequenceToken)
        {
            // The indicators '[' and '{' may start a simple key.

            SaveSimpleKey();

            // Increase the flow level.

            IncreaseFlowLevel();

            // A simple key may follow the indicators '[' and '{'.

            simpleKeyAllowed = true;

            // Consume the token.

            Mark start = mark;
            Skip();

            // Create the FLOW-SEQUENCE-START of FLOW-MAPPING-START token.

            Token token;
            if (isSequenceToken)
            {
                token = new FlowSequenceStart(start, start);
            }
            else
            {
                token = new FlowMappingStart(start, start);
            }

            tokens.Enqueue(token);
        }

        /// <summary>
        /// Increase the flow level and resize the simple key list if needed.
        /// </summary>

        private void IncreaseFlowLevel()
        {
            // Reset the simple key on the next level.

            simpleKeys.Push(new SimpleKey());

            // Increase the flow level.

            ++flowLevel;
        }

        /// <summary>
        /// Produce the FLOW-SEQUENCE-END or FLOW-MAPPING-END token.
        /// </summary>

        private void FetchFlowCollectionEnd(bool isSequenceToken)
        {
            // Reset any potential simple key on the current flow level.

            RemoveSimpleKey();

            // Decrease the flow level.

            DecreaseFlowLevel();

            // No simple keys after the indicators ']' and '}'.

            simpleKeyAllowed = false;

            // Consume the token.

            Mark start = mark;
            Skip();

            Token token;
            if (isSequenceToken)
            {
                token = new FlowSequenceEnd(start, start);
            }
            else
            {
                token = new FlowMappingEnd(start, start);
            }

            tokens.Enqueue(token);
        }

        /// <summary>
        /// Decrease the flow level.
        /// </summary>

        private void DecreaseFlowLevel()
        {
            Debug.Assert(flowLevel > 0, "Could flowLevel be zero when this method is called?");
            if (flowLevel > 0)
            {
                --flowLevel;
                simpleKeys.Pop();
            }
        }

        /// <summary>
        /// Produce the FLOW-ENTRY token.
        /// </summary>

        private void FetchFlowEntry()
        {
            // Reset any potential simple keys on the current flow level.

            RemoveSimpleKey();

            // Simple keys are allowed after ','.

            simpleKeyAllowed = true;

            // Consume the token.

            Mark start = mark;
            Skip();

            // Create the FLOW-ENTRY token and append it to the queue.

            tokens.Enqueue(new FlowEntry(start, mark));
        }

        /// <summary>
        /// Produce the BLOCK-ENTRY token.
        /// </summary>

        private void FetchBlockEntry()
        {
            // Check if the scanner is in the block context.

            if (flowLevel == 0)
            {
                // Check if we are allowed to start a new entry.

                if (!simpleKeyAllowed)
                {
                    throw new SyntaxErrorException("Block sequence entries are not allowed in this context.", mark);
                }

                // Add the BLOCK-SEQUENCE-START token if needed.
                RollIndent(mark.Column, -1, true, mark);
            }
            else
            {

                // It is an error for the '-' indicator to occur in the flow context,
                // but we let the Parser detect and report about it because the Parser
                // is able to point to the context.

            }

            // Reset any potential simple keys on the current flow level.

            RemoveSimpleKey();

            // Simple keys are allowed after '-'.

            simpleKeyAllowed = true;

            // Consume the token.

            Mark start = mark;
            Skip();

            // Create the BLOCK-ENTRY token and append it to the queue.

            tokens.Enqueue(new BlockEntry(start, mark));
        }

        /// <summary>
        /// Produce the KEY token.
        /// </summary>

        private void FetchKey()
        {
            // In the block context, additional checks are required.

            if (flowLevel == 0)
            {
                // Check if we are allowed to start a new key (not nessesary simple).

                if (!simpleKeyAllowed)
                {
                    throw new SyntaxErrorException("Mapping keys are not allowed in this context.", mark);
                }

                // Add the BLOCK-MAPPING-START token if needed.

                RollIndent(mark.Column, -1, false, mark);
            }

            // Reset any potential simple keys on the current flow level.

            RemoveSimpleKey();

            // Simple keys are allowed after '?' in the block context.

            simpleKeyAllowed = flowLevel == 0;

            // Consume the token.

            Mark start = mark;
            Skip();

            // Create the KEY token and append it to the queue.

            tokens.Enqueue(new Key(start, mark));
        }

        /// <summary>
        /// Produce the VALUE token.
        /// </summary>

        private void FetchValue()
        {
            SimpleKey simpleKey = simpleKeys.Peek();

            // Have we found a simple key?

            Console.WriteLine("SIMPLE_KEY_POSSIBLE({0}, {1})", simpleKey.IsPossible, simpleKey.TokenNumber);

            if (simpleKey.IsPossible)
            {
                // Create the KEY token and insert it into the queue.

                Console.Write("QUEUE_INSERT({0}, {1})\n", simpleKey.TokenNumber - tokensParsed, simpleKey.TokenNumber);

                tokens.Insert(simpleKey.TokenNumber - tokensParsed, new Key(simpleKey.Mark, simpleKey.Mark));

                // In the block context, we may need to add the BLOCK-MAPPING-START token.

                RollIndent(simpleKey.Mark.Column, simpleKey.TokenNumber, false, simpleKey.Mark);

                // Remove the simple key.

                simpleKey.IsPossible = false;

                // A simple key cannot follow another simple key.

                simpleKeyAllowed = false;
            }
            else
            {
                // The ':' indicator follows a complex key.

                // In the block context, extra checks are required.

                if (flowLevel == 0)
                {
                    // Check if we are allowed to start a complex value.

                    if (!simpleKeyAllowed)
                    {
                        throw new SyntaxErrorException("Mapping values are not allowed in this context.", mark);
                    }

                    // Add the BLOCK-MAPPING-START token if needed.

                    RollIndent(mark.Column, -1, false, mark);
                }

                // Simple keys after ':' are allowed in the block context.

                simpleKeyAllowed = flowLevel == 0;
            }

            // Consume the token.

            Mark start = mark;
            Skip();

            // Create the VALUE token and append it to the queue.

            tokens.Enqueue(new Value(start, mark));
        }

        /// <summary>
        /// Push the current indentation level to the stack and set the new level
        /// the current column is greater than the indentation level.  In this case,
        /// append or insert the specified token into the token queue.
        /// </summary>
        private void RollIndent(int column, int number, bool isSequence, Mark mark)
        {
            // In the flow context, do nothing.

            if (flowLevel > 0)
            {
                return;
            }

            if (indent < column)
            {

                // Push the current indentation level to the stack and set the new
                // indentation level.


                indents.Push(indent);

                indent = column;

                // Create a token and insert it into the queue.

                Token token;
                if (isSequence)
                {
                    token = new BlockSequenceStart(mark, mark);
                }
                else
                {
                    token = new BlockMappingStart(mark, mark);
                }

                if (number == -1)
                {
                    tokens.Enqueue(token);
                }
                else
                {
                    Console.Write("QUEUE_INSERT({0})\n", number - tokensParsed);
                    tokens.Insert(number - tokensParsed, token);
                }
            }
        }

        /// <summary>
        /// Produce the ALIAS or ANCHOR token.
        /// </summary>

        private void FetchAnchor(bool isAlias)
        {
            // An anchor or an alias could be a simple key.

            SaveSimpleKey();

            // A simple key cannot follow an anchor or an alias.

            simpleKeyAllowed = false;

            // Create the ALIAS or ANCHOR token and append it to the queue.

            tokens.Enqueue(ScanAnchor(isAlias));
        }

        private Token ScanAnchor(bool isAlias)
        {
            // Eat the indicator character.

            Mark start = mark;

            Skip();

            // Consume the value.

            StringBuilder value = new StringBuilder();
            while (IsAlpha())
            {
                value.Append(ReadCurrentCharacter());
            }


            // Check if length of the anchor is greater than 0 and it is followed by
            // a whitespace character or one of the indicators:

            //      '?', ':', ',', ']', '}', '%', '@', '`'.


            if (value.Length == 0 || !(IsBlankOrBreakOrZero() || Check("?:,]}%@`")))
            {
                throw new SyntaxErrorException("While scanning an anchor or alias, did not find expected alphabetic or numeric character.", start);
            }

            // Create a token.

            if (isAlias)
            {
                return new AnchorAlias(value.ToString());
            }
            else
            {
                return new Anchor(value.ToString());
            }
        }

        /// <summary>
        /// Produce the TAG token.
        /// </summary>

        private void FetchTag()
        {
            // A tag could be a simple key.

            SaveSimpleKey();

            // A simple key cannot follow a tag.

            simpleKeyAllowed = false;

            // Create the TAG token and append it to the queue.

            tokens.Enqueue(ScanTag());
        }

        /// <summary>
        /// Scan a TAG token.
        /// </summary>

        Token ScanTag()
        {
            Mark start = mark;

            // Check if the tag is in the canonical form.

            string handle;
            string suffix;

            if (Check('<', 1))
            {
                // Set the handle to ''

                handle = string.Empty;

                // Eat '!<'

                Skip();
                Skip();

                // Consume the tag value.

                suffix = ScanTagUri(null, start);

                // Check for '>' and eat it.

                if (!Check('>'))
                {
                    throw new SyntaxErrorException("While scanning a tag, did not find the expected '>'.", start);
                }

                Skip();
            }
            else
            {
                // The tag has either the '!suffix' or the '!handle!suffix' form.

                // First, try to scan a handle.

                string firstPart = ScanTagHandle(false, start);

                // Check if it is, indeed, handle.

                if (firstPart.Length > 1 && firstPart[0] == '!' && firstPart[firstPart.Length - 1] == '!')
                {
                    handle = firstPart;

                    // Scan the suffix now.

                    suffix = ScanTagUri(null, start);
                }
                else
                {
                    // It wasn't a handle after all.  Scan the rest of the tag.

                    suffix = ScanTagUri(null, start);

                    ScanTagUri(firstPart, start);

                    // Set the handle to '!'.

                    handle = "!";


                    // A special case: the '!' tag.  Set the handle to '' and the
                    // suffix to '!'.


                    if (suffix.Length == 0)
                    {
                        suffix = handle;
                        handle = string.Empty;
                    }
                }
            }

            // Check the character which ends the tag.

            if (!IsBlankOrBreakOrZero())
            {
                throw new SyntaxErrorException("While scanning a tag, did not found expected whitespace or line break.", start);
            }

            // Create a token.

            return new Tag(handle, suffix, start, mark);
        }

        /// <summary>
        /// Produce the SCALAR(...,literal) or SCALAR(...,folded) tokens.
        /// </summary>

        private void FetchBlockScalar(bool isLiteral)
        {
            // Remove any potential simple keys.

            RemoveSimpleKey();

            // A simple key may follow a block scalar.

            simpleKeyAllowed = true;

            // Create the SCALAR token and append it to the queue.

            tokens.Enqueue(ScanBlockScalar(isLiteral));
        }

        /// <summary>
        /// Scan a block scalar.
        /// </summary>

        Token ScanBlockScalar(bool isLiteral)
        {
            StringBuilder value = new StringBuilder();
            StringBuilder leadingBreak = new StringBuilder();
            StringBuilder trailingBreaks = new StringBuilder();

            int chomping = 0;
            int increment = 0;
            int currentIndent = 0;
            bool leadingBlank = false;
            bool trailingBlank = false;

            // Eat the indicator '|' or '>'.

            Mark start = mark;

            Skip();

            // Check for a chomping indicator.

            if (Check("+-"))
            {
                // Set the chomping method and eat the indicator.

                chomping = Check('+') ? +1 : -1;

                Skip();

                // Check for an indentation indicator.

                if (IsDigit())
                {
                    // Check that the intendation is greater than 0.

                    if (Check('0'))
                    {
                        throw new SyntaxErrorException("While scanning a block scalar, found an intendation indicator equal to 0.", start);
                    }

                    // Get the intendation level and eat the indicator.

                    increment = AsDigit();

                    Skip();
                }
            }

            // Do the same as above, but in the opposite order.

            else if (IsDigit())
            {
                if (Check('0'))
                {
                    throw new SyntaxErrorException("While scanning a block scalar, found an intendation indicator equal to 0.", start);
                }

                increment = AsDigit();

                Skip();

                if (Check("+-"))
                {
                    chomping = Check('+') ? +1 : -1;

                    Skip();
                }
            }

            // Eat whitespaces and comments to the end of the line.

            while (IsBlank())
            {
                Skip();
            }

            if (Check('#'))
            {
                while (!IsBreakOrZero())
                {
                    Skip();
                }
            }

            // Check if we are at the end of the line.

            if (!IsBreakOrZero())
            {
                throw new SyntaxErrorException("While scanning a block scalar, did not found expected comment or line break.", start);
            }

            // Eat a line break.

            if (IsBreak())
            {
                SkipLine();
            }

            Mark end = mark;

            // Set the intendation level if it was specified.

            if (increment != 0)
            {
                currentIndent = indent >= 0 ? indent + increment : increment;
            }

            // Scan the leading line breaks and determine the indentation level if needed.

            currentIndent = ScanBlockScalarBreaks(currentIndent, trailingBreaks, start, ref end);

            // Scan the block scalar content.

            while (mark.Column == currentIndent && !IsZero())
            {

                // We are at the beginning of a non-empty line.


                // Is it a trailing whitespace?

                trailingBlank = IsBlank();

                // Check if we need to fold the leading line break.

                if (!isLiteral && StartsWith(leadingBreak, '\n') && !leadingBlank && !trailingBlank)
                {
                    // Do we need to join the lines by space?

                    if (trailingBreaks.Length == 0)
                    {
                        value.Append(' ');
                    }

                    leadingBreak.Length = 0;
                }
                else
                {
                    value.Append(leadingBreak.ToString());
                    leadingBreak.Length = 0;
                }

                // Append the remaining line breaks.

                value.Append(trailingBreaks.ToString());
                trailingBreaks.Length = 0;

                // Is it a leading whitespace?

                leadingBlank = IsBlank();

                // Consume the current line.

                while (!IsBreakOrZero())
                {
                    value.Append(ReadCurrentCharacter());
                }

                // Consume the line break.

                leadingBreak.Append(ReadLine());

                // Eat the following intendation spaces and line breaks.

                currentIndent = ScanBlockScalarBreaks(currentIndent, trailingBreaks, start, ref end);
            }

            // Chomp the tail.

            if (chomping != -1)
            {
                value.Append(leadingBreak);
            }
            if (chomping == 1)
            {
                value.Append(trailingBreaks);
            }

            // Create a token.

            ScalarStyle style = isLiteral ? ScalarStyle.Literal : ScalarStyle.Folded;
            return new Scalar(value.ToString(), style, start, end);
        }

        /// <summary>
        /// Scan intendation spaces and line breaks for a block scalar.  Determine the
        /// intendation level if needed.
        /// </summary>

        private int ScanBlockScalarBreaks(int currentIndent, StringBuilder breaks, Mark start, ref Mark end)
        {
            int maxIndent = 0;

            end = mark;

            // Eat the intendation spaces and line breaks.

            for (; ; )
            {
                // Eat the intendation spaces.

                while ((currentIndent == 0 || mark.Column < currentIndent) && IsSpace())
                {
                    Skip();
                }

                if (mark.Column > maxIndent)
                {
                    maxIndent = mark.Column;
                }

                // Check for a tab character messing the intendation.

                if ((currentIndent == 0 || mark.Column < currentIndent) && IsTab())
                {
                    throw new SyntaxErrorException("While scanning a block scalar, found a tab character where an intendation space is expected.", start);
                }

                // Have we found a non-empty line?

                if (!IsBreak())
                {
                    break;
                }

                // Consume the line break.

                breaks.Append(ReadLine());

                end = mark;
            }

            // Determine the indentation level if needed.

            if (currentIndent == 0)
            {
                currentIndent = Math.Max(maxIndent, Math.Max(indent + 1, 1));
            }

            return currentIndent;
        }

        /// <summary>
        /// Produce the SCALAR(...,single-quoted) or SCALAR(...,double-quoted) tokens.
        /// </summary>

        private void FetchFlowScalar(bool isSingleQuoted)
        {
            // A plain scalar could be a simple key.

            SaveSimpleKey();

            // A simple key cannot follow a flow scalar.

            simpleKeyAllowed = false;

            // Create the SCALAR token and append it to the queue.

            tokens.Enqueue(ScanFlowScalar(isSingleQuoted));
        }

        /// <summary>
        /// Scan a quoted scalar.
        /// </summary>

        private Token ScanFlowScalar(bool isSingleQuoted)
        {
            // Eat the left quote.

            Mark start = mark;

            Skip();

            // Consume the content of the quoted scalar.

            StringBuilder value = new StringBuilder();
            StringBuilder whitespaces = new StringBuilder();
            StringBuilder leadingBreak = new StringBuilder();
            StringBuilder trailingBreaks = new StringBuilder();
            for (; ; )
            {
                // Check that there are no document indicators at the beginning of the line.

                buffer.Cache(4);

                if (IsDocumentIndicator())
                {
                    throw new SyntaxErrorException("While scanning a quoted scalar, found unexpected document indicator.", start);
                }

                // Check for EOF.

                if (IsZero())
                {
                    throw new SyntaxErrorException("While scanning a quoted scalar, found unexpected end of stream.", start);
                }

                // Consume non-blank characters.

                bool hasLeadingBlanks = false;

                while (!IsBlankOrBreakOrZero())
                {
                    // Check for an escaped single quote.

                    if (isSingleQuoted && Check('\'', 0) && Check('\'', 1))
                    {
                        value.Append('\'');
                        Skip();
                        Skip();
                    }

                    // Check for the right quote.

                    else if (Check(isSingleQuoted ? '\'' : '"'))
                    {
                        break;
                    }

                    // Check for an escaped line break.

                    else if (!isSingleQuoted && Check('\\') && IsBreak(1))
                    {
                        Skip();
                        SkipLine();
                        hasLeadingBlanks = true;
                        break;
                    }

                    // Check for an escape sequence.

                    else if (!isSingleQuoted && Check('\\'))
                    {
                        int codeLength = 0;

                        // Check the escape character.

                        char escapeCharacter = buffer.Peek(1);
                        switch (escapeCharacter)
                        {
                            case 'x':
                                codeLength = 2;
                                break;

                            case 'u':
                                codeLength = 4;
                                break;

                            case 'U':
                                codeLength = 8;
                                break;

                            default:
                                char unescapedCharacter;
                                if (simpleEscapeCodes.TryGetValue(escapeCharacter, out unescapedCharacter))
                                {
                                    value.Append(unescapedCharacter);
                                }
                                else
                                {
                                    throw new SyntaxErrorException("While parsing a quoted scalar, found unknown escape character.", start);
                                }
                                break;
                        }

                        Skip();
                        Skip();

                        // Consume an arbitrary escape code.

                        if (codeLength > 0)
                        {
                            uint character = 0;

                            // Scan the character value.

                            for (int k = 0; k < codeLength; ++k)
                            {
                                if (!IsHex(k))
                                {
                                    throw new SyntaxErrorException("While parsing a quoted scalar, did not find expected hexdecimal number.", start);
                                }
                                character = (uint)((character << 4) + AsHex(k));
                            }

                            // Check the value and write the character.

                            if ((character >= 0xD800 && character <= 0xDFFF) || character > 0x10FFFF)
                            {
                                throw new SyntaxErrorException("While parsing a quoted scalar, found invalid Unicode character escape code.", start);
                            }

                            value.Append((char)character);

                            // Advance the pointer.

                            for (int k = 0; k < codeLength; ++k)
                            {
                                Skip();
                            }
                        }
                    }
                    else
                    {
                        // It is a non-escaped non-blank character.

                        value.Append(ReadCurrentCharacter());
                    }
                }

                // Check if we are at the end of the scalar.

                if (Check(isSingleQuoted ? '\'' : '"'))
                    break;

                // Consume blank characters.

                while (IsBlank() || IsBreak())
                {
                    if (IsBlank())
                    {
                        // Consume a space or a tab character.

                        if (!hasLeadingBlanks)
                        {
                            whitespaces.Append(ReadCurrentCharacter());
                        }
                        else
                        {
                            Skip();
                        }
                    }
                    else
                    {
                        // Check if it is a first line break.

                        if (!hasLeadingBlanks)
                        {
                            whitespaces.Length = 0;
                            leadingBreak.Append(ReadLine());
                            hasLeadingBlanks = true;
                        }
                        else
                        {
                            trailingBreaks.Append(ReadLine());
                        }
                    }
                }

                // Join the whitespaces or fold line breaks.

                if (hasLeadingBlanks)
                {
                    // Do we need to fold line breaks?

                    if (StartsWith(leadingBreak, '\n'))
                    {
                        if (trailingBreaks.Length == 0)
                        {
                            value.Append(' ');
                        }
                        else
                        {
                            value.Append(trailingBreaks.ToString());
                        }
                    }
                    else
                    {
                        value.Append(leadingBreak.ToString());
                        value.Append(trailingBreaks.ToString());
                    }
                    leadingBreak.Length = 0;
                    trailingBreaks.Length = 0;
                }
                else
                {
                    value.Append(whitespaces.ToString());
                    whitespaces.Length = 0;
                }
            }

            // Eat the right quote.

            Skip();

            return new Scalar(value.ToString(), isSingleQuoted ? ScalarStyle.SingleQuoted : ScalarStyle.DoubleQuoted);
        }

        /// <summary>
        /// Produce the SCALAR(...,plain) token.
        /// </summary>

        private void FetchPlainScalar()
        {
            // A plain scalar could be a simple key.

            SaveSimpleKey();

            // A simple key cannot follow a flow scalar.

            simpleKeyAllowed = false;

            // Create the SCALAR token and append it to the queue.

            tokens.Enqueue(ScanPlainScalar());
        }

        /// <summary>
        /// Scan a plain scalar.
        /// </summary>

        private Token ScanPlainScalar()
        {
            StringBuilder value = new StringBuilder();
            StringBuilder whitespaces = new StringBuilder();
            StringBuilder leadingBreak = new StringBuilder();
            StringBuilder trailingBreaks = new StringBuilder();

            bool hasLeadingBlanks = false;
            int currentIndent = indent + 1;

            Mark start = mark;
            Mark end = mark;

            // Consume the content of the plain scalar.

            for (; ; )
            {
                // Check for a document indicator.

                if (IsDocumentIndicator())
                {
                    break;
                }

                // Check for a comment.

                if (Check('#'))
                {
                    break;
                }

                // Consume non-blank characters.
                while (!IsBlankOrBreakOrZero())
                {
                    // Check for 'x:x' in the flow context. TODO: Fix the test "spec-08-13".

                    if (flowLevel > 0 && Check(':') && !IsBlankOrBreakOrZero(1))
                    {
                        throw new SyntaxErrorException("While scanning a plain scalar, found unexpected ':'.", start);
                    }

                    // Check for indicators that may end a plain scalar.

                    if ((Check(':') && IsBlankOrBreakOrZero(1)) || (flowLevel > 0 && Check(",:?[]{}")))
                    {
                        break;
                    }

                    // Check if we need to join whitespaces and breaks.

                    if (hasLeadingBlanks || whitespaces.Length > 0)
                    {
                        if (hasLeadingBlanks)
                        {
                            // Do we need to fold line breaks?

                            if (StartsWith(leadingBreak, '\n'))
                            {
                                if (trailingBreaks.Length == 0)
                                {
                                    value.Append(' ');
                                }
                                else
                                {
                                    value.Append(trailingBreaks);
                                }
                            }
                            else
                            {
                                value.Append(leadingBreak);
                                value.Append(trailingBreaks);
                            }

                            leadingBreak.Length = 0;
                            trailingBreaks.Length = 0;

                            hasLeadingBlanks = false;
                        }
                        else
                        {
                            value.Append(whitespaces);
                            whitespaces.Length = 0;
                        }
                    }

                    // Copy the character.

                    value.Append(ReadCurrentCharacter());

                    end = mark;
                }

                // Is it the end?

                if (!(IsBlank() || IsBreak()))
                {
                    break;
                }

                // Consume blank characters.

                while (IsBlank() || IsBreak())
                {
                    if (IsBlank())
                    {
                        // Check for tab character that abuse intendation.

                        if (hasLeadingBlanks && mark.Column < currentIndent && IsTab())
                        {
                            throw new SyntaxErrorException("While scanning a plain scalar, found a tab character that violate intendation.", start);
                        }

                        // Consume a space or a tab character.

                        if (!hasLeadingBlanks)
                        {
                            value.Append(ReadCurrentCharacter());
                        }
                        else
                        {
                            Skip();
                        }
                    }
                    else
                    {
                        // Check if it is a first line break.

                        if (!hasLeadingBlanks)
                        {
                            whitespaces.Length = 0;
                            leadingBreak.Append(ReadLine());
                            hasLeadingBlanks = true;
                        }
                        else
                        {
                            trailingBreaks.Append(ReadLine());
                        }
                    }
                }

                // Check intendation level.

                if (flowLevel == 0 && mark.Column < currentIndent)
                {
                    break;
                }
            }

            // Note that we change the 'simple_key_allowed' flag.

            if (hasLeadingBlanks)
            {
                simpleKeyAllowed = true;
            }

            // Create a token.

            return new Scalar(value.ToString(), ScalarStyle.Plain, start, end);
        }


        /// <summary>
        /// Remove a potential simple key at the current flow level.
        /// </summary>

        private void RemoveSimpleKey()
        {
            SimpleKey key = simpleKeys.Peek();

            if (key.IsPossible && key.IsRequired)
            {
                // If the key is required, it is an error.

                throw new SyntaxErrorException("While scanning a simple key, could not found expected ':'.", key.Mark);
            }

            // Remove the key from the stack.

            key.IsPossible = false;
        }

        /// <summary>
        /// Scan the directive name.
        ///
        /// Scope:
        ///      %YAML   1.1     # a comment \n
        ///       ^^^^
        ///      %TAG    !yaml!  tag:yaml.org,2002:  \n
        ///       ^^^
        /// </summary>
        private string ScanDirectiveName(Mark start)
        {
            StringBuilder name = new StringBuilder();

            // Consume the directive name.

            buffer.Cache(1);

            while (IsAlpha())
            {
                name.Append(ReadCurrentCharacter());
                buffer.Cache(1);
            }

            // Check if the name is empty.

            if (name.Length == 0)
            {
                throw new SyntaxErrorException("While scanning a directive, could not find expected directive name.", start);
            }

            // Check for an blank character after the name.

            if (!IsBlankOrBreakOrZero())
            {
                throw new SyntaxErrorException("While scanning a directive, found unexpected non-alphabetical character.", start);
            }

            return name.ToString();
        }

        private void SkipWhitespaces()
        {
            // Eat whitespaces.

            buffer.Cache(1);

            while (IsBlank())
            {
                Skip();
                buffer.Cache(1);
            }
        }

        /// <summary>
        /// Scan the value of VERSION-DIRECTIVE.
        ///
        /// Scope:
        ///      %YAML   1.1     # a comment \n
        ///           ^^^^^^
        /// </summary>
        private Token ScanVersionDirectiveValue(Mark start)
        {
            SkipWhitespaces();

            // Consume the major version number.

            int major = ScanVersionDirectiveNumber(start);

            // Eat '.'.

            if (!Check('.'))
            {
                throw new SyntaxErrorException("While scanning a %YAML directive, did not find expected digit or '.' character.", start);
            }

            Skip();

            // Consume the minor version number.

            int minor = ScanVersionDirectiveNumber(start);

            return new VersionDirective(new Version(major, minor), start, start);
        }

        /// <summary>
        /// Scan the value of a TAG-DIRECTIVE token.
        ///
        /// Scope:
        ///      %TAG    !yaml!  tag:yaml.org,2002:  \n
        ///          ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
        /// </summary>
        private Token ScanTagDirectiveValue(Mark start)
        {
            SkipWhitespaces();

            // Scan a handle.

            string handle = ScanTagHandle(true, start);

            // Expect a whitespace.

            buffer.Cache(1);

            if (!IsBlank())
            {
                throw new SyntaxErrorException("While scanning a %TAG directive, did not find expected whitespace.", start);
            }

            SkipWhitespaces();

            // Scan a prefix.

            string prefix = ScanTagUri(null, start);

            // Expect a whitespace or line break.

            buffer.Cache(1);

            if (!IsBlankOrBreakOrZero())
            {
                throw new SyntaxErrorException("While scanning a %TAG directive, did not find expected whitespace or line break.", start);
            }

            return new TagDirective(handle, prefix, start, start);
        }

        /// <summary>
        /// Scan a tag.
        /// </summary>

        private string ScanTagUri(string head, Mark start)
        {
            StringBuilder tag = new StringBuilder();
            if (head != null && head.Length > 1)
            {
                tag.Append(head.Substring(1));
            }

            // Scan the tag.

            buffer.Cache(1);


            // The set of characters that may appear in URI is as follows:

            //      '0'-'9', 'A'-'Z', 'a'-'z', '_', '-', ';', '/', '?', ':', '@', '&',
            //      '=', '+', '$', ',', '.', '!', '~', '*', '\'', '(', ')', '[', ']',
            //      '%'.


            while (IsAlpha() || Check(";/?:@&=+$,.!~*'()[]%"))
            {
                // Check if it is a URI-escape sequence.

                if (Check('%'))
                {
                    tag.Append(ScanUriEscapes(start));
                }
                else
                {
                    tag.Append(ReadCurrentCharacter());
                }

                buffer.Cache(1);
            }

            // Check if the tag is non-empty.

            if (tag.Length == 0)
            {
                throw new SyntaxErrorException("While parsing a tag, did not find expected tag URI.", start);
            }

            return tag.ToString();
        }

        /// <summary>
        /// Decode an URI-escape sequence corresponding to a single UTF-8 character.
        /// </summary>

        private char ScanUriEscapes(Mark start)
        {
            // Decode the required number of characters.

            List<byte> charBytes = new List<byte>();
            int width = 0;
            do
            {
                // Check for a URI-escaped octet.

                buffer.Cache(3);

                if (!(Check('%') && IsHex(1) && IsHex(2)))
                {
                    throw new SyntaxErrorException("While parsing a tag, did not find URI escaped octet.", start);
                }

                // Get the octet.

                int octet = (AsHex(1) << 4) + AsHex(2);

                // If it is the leading octet, determine the length of the UTF-8 sequence.

                if (width == 0)
                {
                    width = (octet & 0x80) == 0x00 ? 1 :
                            (octet & 0xE0) == 0xC0 ? 2 :
                            (octet & 0xF0) == 0xE0 ? 3 :
                            (octet & 0xF8) == 0xF0 ? 4 : 0;

                    if (width == 0)
                    {
                        throw new SyntaxErrorException("While parsing a tag, found an incorrect leading UTF-8 octet.", start);
                    }
                }
                else
                {
                    // Check if the trailing octet is correct.

                    if ((octet & 0xC0) != 0x80)
                    {
                        throw new SyntaxErrorException("While parsing a tag, found an incorrect trailing UTF-8 octet.", start);
                    }
                }

                // Copy the octet and move the pointers.

                charBytes.Add((byte)octet);

                Skip();
                Skip();
                Skip();
            } while (--width > 0);

            char[] characters = Encoding.UTF8.GetChars(charBytes.ToArray());

            if (characters.Length != 1)
            {
                throw new SyntaxErrorException("While parsing a tag, found an incorrect UTF-8 sequence.", start);
            }

            return characters[0];
        }

        /// <summary>
        /// Scan a tag handle.
        /// </summary>

        private string ScanTagHandle(bool isDirective, Mark start)
        {

            // Check the initial '!' character.

            buffer.Cache(1);

            if (!Check('!'))
            {
                throw new SyntaxErrorException("While scanning a tag, did not find expected '!'.", start);
            }

            // Copy the '!' character.

            StringBuilder tagHandle = new StringBuilder();
            tagHandle.Append(ReadCurrentCharacter());

            // Copy all subsequent alphabetical and numerical characters.

            buffer.Cache(1);
            while (IsAlpha())
            {
                tagHandle.Append(ReadCurrentCharacter());
                buffer.Cache(1);
            }

            // Check if the trailing character is '!' and copy it.

            if (Check('!'))
            {
                tagHandle.Append(ReadCurrentCharacter());
            }
            else
            {

                // It's either the '!' tag or not really a tag handle.  If it's a %TAG
                // directive, it's an error.  If it's a tag token, it must be a part of
                // URI.


                if (isDirective && (tagHandle.Length != 1 || tagHandle[0] != '!'))
                {
                    throw new SyntaxErrorException("While parsing a tag directive, did not find expected '!'.", start);
                }
            }

            return tagHandle.ToString();
        }

        /// <summary>
        /// Scan the version number of VERSION-DIRECTIVE.
        ///
        /// Scope:
        ///      %YAML   1.1     # a comment \n
        ///              ^
        ///      %YAML   1.1     # a comment \n
        ///                ^
        /// </summary>
        private int ScanVersionDirectiveNumber(Mark start)
        {
            int value = 0;
            int length = 0;

            // Repeat while the next character is digit.

            buffer.Cache(1);

            while (IsDigit())
            {
                // Check if the number is too long.

                if (++length > MaxVersionNumberLength)
                {
                    throw new SyntaxErrorException("While scanning a %YAML directive, found extremely long version number.", start);
                }

                value = value * 10 + AsDigit();

                Skip();

                buffer.Cache(1);
            }

            // Check if the number was present.

            if (length == 0)
            {
                throw new SyntaxErrorException("While scanning a %YAML directive, did not find expected version number.", start);
            }

            return value;
        }

        /// <summary>
        /// Check if a simple key may start at the current position and add it if
        /// needed.
        /// </summary>

        private void SaveSimpleKey()
        {

            // A simple key is required at the current position if the scanner is in
            // the block context and the current column coincides with the indentation
            // level.


            bool isRequired = (flowLevel == 0 && indent == mark.Column);


            // A simple key is required only when it is the first token in the current
            // line.  Therefore it is always allowed.  But we add a check anyway.


            Debug.Assert(simpleKeyAllowed || !isRequired, "Can't require a simple key and disallow it at the same time.");    // Impossible.


            // If the current position may start a simple key, save it.


            if (simpleKeyAllowed)
            {
                Console.WriteLine("PUSH_SIMPLE_KEY({0}, {1})", tokensParsed + tokens.Count, tokensParsed);
                SimpleKey key = new SimpleKey(true, isRequired, tokensParsed + tokens.Count, mark);

                RemoveSimpleKey();

                simpleKeys.Pop();
                simpleKeys.Push(key);
            }
        }
    }
}