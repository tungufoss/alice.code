using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ALICE
{
    public class RetraceSet : TrainingSet
    {
        public int NumApplied;

        public RetraceSet(string distribution, string dimension, Trajectory track, int iter, bool extended, 
            int numFeat, int model, string stepwiseBias, Features.Mode featureMode, DirectoryInfo data)
            : base(distribution, dimension, track, iter, extended, numFeat, model, stepwiseBias, data)
        {
            Read();
            FeatureMode = featureMode;

            if (FeatureMode != Features.Mode.Local)
                FileInfo =
                    new FileInfo(FileInfo.FullName.Replace(Features.Mode.Local.ToString(), FeatureMode.ToString()));
        }

        private void Read()
        {
            List<string> header;
            List<string[]> content = CSV.Read(FileInfo, out header);
            if (content == null || content.Count == 0) return;

            int iPID = header.FindIndex(x => x.Equals("PID"));
            int iStep = header.FindIndex(x => x.Equals("Step"));
            int iDispatch = header.FindIndex(x => x.Equals("Dispatch"));
            int iFollowed = header.FindIndex(x => x.Equals("Followed"));
            int iResultingOptMakespan = header.FindIndex(x => x.Equals("ResultingOptMakespan"));
            int iRank = header.FindIndex(x => x.Equals("Rank"));

            int minStep = Convert.ToInt32(content[0][iStep]);

            int pid, step;
            for (pid = 1; pid <= AlreadySavedPID; pid++)
                for (step = 0; step < NumDimension; step++)
                    Preferences[pid - 1, step] = new List<Preference>();

            foreach (var line in content)
            {
                pid = Convert.ToInt32(line[iPID]);
                if (pid > AlreadySavedPID) return;

                step = Convert.ToInt32(line[iStep]);
                bool followed = Convert.ToInt32(line[iFollowed]) == 1;
                int resultingOptMakespan = Convert.ToInt32(line[iResultingOptMakespan]);
                if (line.Length <= iRank) iRank = -1;
                int rank = iRank >= 0 ? Convert.ToInt32(line[iRank]) : 0;

                Schedule.Dispatch dispatch = new Schedule.Dispatch(line[iDispatch]);
                Preferences[pid - 1, step - minStep].Add(new Preference(dispatch, followed, resultingOptMakespan, rank));
            }

            if (iRank >= 0) return;
            for (pid = 1; pid < AlreadySavedPID; pid++)
                RankPreferences(pid);
        }

        public new void Write()
        {
            if (NumApplied == AlreadySavedPID)
                Write(FileMode.Create, Preferences);
        }

        internal void ApplyAll(Func<int, Func<int, int, Schedule, int>, string> applyFunc,
            Func<int, int, Schedule, int> innerFunc, List<Preference>[,] writeData,
            Func<int> overwriteWriteFunc = null)
        {
            for (int pid = 1; pid <= AlreadySavedPID; pid++)
            {
                applyFunc(pid, innerFunc);
                NumApplied++;
            }

            if (overwriteWriteFunc != null)
                overwriteWriteFunc();
            else if (writeData != null)
                Write(FileMode.Create, writeData);
        }

        public new void Apply()
        {
            ApplyAll(Retrace, null, Preferences);
        }

        public new string Apply(int pid)
        {
            NumApplied++;
            return Retrace(pid, NumDimension < 100 && FeatureMode == Features.Mode.Local, null);
        }

        internal string Retrace(int pid, Func<int, int, Schedule, int> applyFunc)
        {
            return Retrace(pid, false, applyFunc);
        }

        private string Retrace(int pid, bool canCollectAndLabel, Func<int, int, Schedule, int> applyFunc)
        {
            if (pid > AlreadySavedPID)
                throw new Exception(String.Format("PID {0} exeeds what has already been created. Cannot retrace!", pid));

            if (Preferences[pid - 1, 0].Count == 0)
            {
                return canCollectAndLabel
                    ? String.Format("{0} - from scratch!", CollectAndLabel(pid))
                    : String.Format("PID {0} doesn't exist!", pid);
            }

            string name = GetName(pid);
            var jssp = GetEmptySchedule(name);
            int currentNumFeatures = 0;
            for (var step = 0; step < NumDimension; step++)
            {
                if (!ValidDispatches(ref Preferences[pid - 1, step], jssp))
                    return canCollectAndLabel
                        ? String.Format("{0} - from scratch!", CollectAndLabel(pid))
                        : String.Format("PID {0} gave an invalid dispatch!", pid);

                currentNumFeatures += Preferences[pid - 1, step].Count;

                int dispatchedJob;
                if (Preferences[pid - 1, step].Count > 0)
                {
                    UpdateFeatures(pid, step, jssp);
                    
                    if (applyFunc != null)
                        applyFunc(pid, step, jssp);

                    var followed = Preferences[pid - 1, step].Find(p => p.Followed);
                    dispatchedJob = followed == null ? jssp.JobChosenBySDR((SDRData.SDR)Track) : followed.Dispatch.Job;
                }
                else
                {
                    dispatchedJob = jssp.ReadyJobs.Count > 1
                        ? jssp.JobChosenBySDR((SDRData.SDR)Track)
                        : jssp.ReadyJobs[0];
                }
                jssp.Dispatch1(dispatchedJob);
            }
            NumFeatures += currentNumFeatures;
            return String.Format("{0}:{1} #{2} phi", FileInfo.Name, pid, currentNumFeatures);
        }

        internal int UpdateFeatures(int pid, int step, Schedule jssp)
        {
            LinearModel dummy = new LinearModel();
            foreach (var p in Preferences[pid - 1, step])
            {
                var lookahead = jssp.Clone();
                p.Feature = lookahead.Dispatch1(p.Dispatch.Job, FeatureMode, dummy);
            }
            return Preferences[pid - 1, step].Count;
        }

        internal bool ValidDispatches(ref List<Preference> prefs, Schedule jssp)
        {
            if (prefs.Count > jssp.ReadyJobs.Count)
            {
                prefs = prefs
                    .GroupBy(x => x.Dispatch.Name)
                    .Select(group => group.First()).ToList();
            }

            if (prefs.Count == 0 && jssp.Sequence.Count >= NumDimension - 1)
                return true;

            if (prefs.Count != jssp.ReadyJobs.Count)
                return false;

            if (prefs.Any(p => p.Dispatch.Mac < 0))
                return false;

            if (prefs.Count(p => p.Followed) == 1)
                return true;

            foreach (Preference pref in prefs)
            {
                jssp.FindDispatch(pref.Dispatch.Job, out pref.Dispatch);
            }
            return false;
        }
    }
}