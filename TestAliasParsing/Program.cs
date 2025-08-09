using System;
using System.Collections.Generic;
using System.Linq;

class Program 
{
    static void Main()
    {
        string output = @"Alias                          Device     Task               File Size    License      Model ID
-----------------------------------------------------------------------------------------------
phi-4                          GPU        chat-completion    8.37 GB      MIT          Phi-4-cuda-gpu      
                               GPU        chat-completion    8.37 GB      MIT          Phi-4-generic-gpu   
                               CPU        chat-completion    10.16 GB     MIT          Phi-4-generic-cpu   
--------------------------------------------------------------------------------------------------------
phi-3-mini-128k                GPU        chat-completion    2.13 GB      MIT          Phi-3-mini-128k-instruct-cuda-gpu
                               GPU        chat-completion    2.13 GB      MIT          Phi-3-mini-128k-instruct-generic-gpu
                               CPU        chat-completion    2.54 GB      MIT          Phi-3-mini-128k-instruct-generic-cpu
--------------------------------------------------------------------------------------------------------------------------
phi-3-mini-4k                  GPU        chat-completion    2.13 GB      MIT          Phi-3-mini-4k-instruct-cuda-gpu
                               GPU        chat-completion    2.13 GB      MIT          Phi-3-mini-4k-instruct-generic-gpu
                               CPU        chat-completion    2.53 GB      MIT          Phi-3-mini-4k-instruct-generic-cpu
-------------------------------------------------------------------------------------------------------------------------
mistral-7b-v0.2                GPU        chat-completion    3.98 GB      apache-2.0   mistralai-Mistral-7B-Instruct-v0-2-cuda-gpu
                               GPU        chat-completion    4.07 GB      apache-2.0   mistralai-Mistral-7B-Instruct-v0-2-generic-gpu
                               CPU        chat-completion    4.07 GB      apache-2.0   mistralai-Mistral-7B-Instruct-v0-2-generic-cpu
------------------------------------------------------------------------------------------------------------------------------
gpt-oss-20b                    GPU        chat-completion    9.65 GB      apache-2.0   gpt-oss-20b-cuda-gpu";

        var aliases = new HashSet<string>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        bool inTable = false;
        foreach (var line in lines)
        {
            if (line.Contains("Alias") && line.Contains("Device") && line.Contains("Task"))
            {
                inTable = true;
                continue;
            }
            if (line.Contains("---") || !inTable)
                continue;
            
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                var alias = parts[0].Trim();
                if (!alias.Contains("GPU") && !alias.Contains("CPU") && 
                    !alias.Contains("chat-completion") && !alias.Contains("GB") &&
                    !alias.Contains("MIT") && !alias.Contains("apache"))
                {
                    aliases.Add(alias);
                }
            }
        }
        
        var result = aliases.OrderBy(a => a).ToList();
        Console.WriteLine($"Found {result.Count} aliases:");
        foreach (var alias in result)
        {
            Console.WriteLine($"  - {alias}");
        }
    }
}
