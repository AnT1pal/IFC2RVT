using System.Linq;
using Autodesk.Revit.DB;

namespace IFC2RVT.Conversion
{
    /// <summary>
    /// Imported geometry produces a steady stream of warnings - overlapping walls, unenclosed
    /// rooms, slightly off-axis joins. Left alone they open a modal dialog per element and the
    /// conversion never finishes. Warnings are discarded; genuine errors roll back only the
    /// offending batch, which the engine then reports.
    /// </summary>
    public class FailureSwallower : IFailuresPreprocessor
    {
        public int WarningsSuppressed { get; private set; }
        public int ErrorsEncountered { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var failures = accessor.GetFailureMessages();
            if (failures.Count == 0) return FailureProcessingResult.Continue;

            var hasErrors = false;

            foreach (var failure in failures)
            {
                var severity = failure.GetSeverity();

                if (severity == FailureSeverity.Warning)
                {
                    accessor.DeleteWarning(failure);
                    WarningsSuppressed++;
                }
                else
                {
                    hasErrors = true;
                    ErrorsEncountered++;

                    // Prefer Revit resolving the error itself over losing the whole batch.
                    if (failure.HasResolutions())
                    {
                        accessor.ResolveFailure(failure);
                    }
                }
            }

            return hasErrors && accessor.GetFailureMessages().Count > 0
                ? FailureProcessingResult.ProceedWithRollBack
                : FailureProcessingResult.ProceedWithCommit;
        }

        /// <summary>Applies this preprocessor plus silent-mode options to a transaction.</summary>
        public void AttachTo(Transaction transaction)
        {
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(this);
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(false);
            transaction.SetFailureHandlingOptions(options);
        }
    }
}
