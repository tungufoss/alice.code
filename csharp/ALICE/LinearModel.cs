using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ALICE
{
    public class LinearModel
    {
        public enum Model
        {
            NotSet,
            SDR,
            CMAES,
            PREF,
            SingleFeat
        }

        public readonly Model Type;

        public readonly string Name;

        public readonly FileInfo FileInfo;
        private readonly int _numFeatures;
        private readonly int _modelID;
        public readonly Features.Mode FeatureMode;

        public double[][] LocalWeights = new double[Features.LocalCount][];
        public readonly double[][] GlobalWeights = new double[Features.GlobalCount][];

        public const double WEIGHT_TOLERANCE = 1e-10;

        public readonly bool TimeIndependent;

        public readonly string Distribution;
        public readonly string Dimension;

        public readonly int Iteration;

        protected LinearModel(FileInfo file, Features.Mode featureMode, int numFeatures, int modelID,
            bool timeIndependent, string distribution, string dimension, Model type)
        {
            FileInfo = file;
            FeatureMode = featureMode;
            _numFeatures = numFeatures;
            _modelID = modelID;
            Name = String.Format("F{0}.M{1}", _numFeatures, _modelID);
            TimeIndependent = timeIndependent;
            Distribution = distribution;
            Dimension = dimension;
            Type = type;
        }

        protected LinearModel(FileInfo file, Features.Mode featureMode, int timeDependentSteps, int numFeatures,
            int modelID, string distribution, string dimension, Model type)
            : this(file, featureMode, numFeatures, modelID, timeDependentSteps == 1, distribution, dimension, type)
        {
            for (int i = 0; i < Features.LocalCount; i++)
                LocalWeights[i] = new double[timeDependentSteps];
            
            if (featureMode != Features.Mode.Global) return;

            for (int i = 0; i < Features.GlobalCount; i++)
                GlobalWeights[i] = new double[timeDependentSteps];
        }

        public LinearModel(SDRData.SDR sdr, string distribution, string dimension)
            : this(null, Features.Mode.Local, 1, 1, (int) sdr, distribution, dimension, Model.SDR)
        {
            Name = String.Format("{0}Equiv", sdr);
            switch (sdr)
            {
                case SDRData.SDR.MWR:
                    LocalWeights[(int) Features.Local.jobWrm][0] = +1;
                    return;
                case SDRData.SDR.LWR:
                    LocalWeights[(int) Features.Local.jobWrm][0] = -1;
                    return;
                case SDRData.SDR.SPT:
                    LocalWeights[(int) Features.Local.proc][0] = -1;
                    return;
                case SDRData.SDR.LPT:
                    LocalWeights[(int) Features.Local.proc][0] = +1;
                    return;
                default:
                    return; // do nothing
            }
        }

        public LinearModel(FileInfo file, int numFeatures, int modelID, string distribution, string dimension)
            : this(
                file,
                Regex.IsMatch(file.Name, Features.Mode.Global.ToString()) ? Features.Mode.Global : Features.Mode.Local,
                numFeatures, modelID, Regex.IsMatch(file.Name, "timeindependent"), distribution, dimension, Model.PREF)
        {
            FileInfo = file;
            LinearModel[] loggedWeights = ReadLoggedLinearWeights(file, distribution, dimension, Model.PREF);

            foreach (var w in loggedWeights.Where(w => w._numFeatures == numFeatures && w._modelID == _modelID))
            {
                LocalWeights = w.LocalWeights;
                return;
            }
            throw new Exception(String.Format("Cannot find weights {0} to user requirements from {1}!", Name, file.Name));
        }

        public LinearModel(object feat, int extremal, DirectoryInfo dataDirInfo)
            : this(
                null, feat is Features.Local ? Features.Mode.Local : Features.Mode.Global, 1,
                (int) feat + 1 + (feat is Features.Local ? 0 : Features.LocalCount),
                extremal, "", "", Model.SingleFeat)
        {
            FileInfo = new FileInfo(String.Format("{0}/{1}/weights/{2}.csv", dataDirInfo.FullName, Type, Name));
            switch (FeatureMode)
            {
                case Features.Mode.Local:
                    LocalWeights[(int) feat][0] = extremal;
                    break;
                case Features.Mode.Global:
                    _numFeatures += Features.LocalCount;
                    GlobalWeights[(int) feat][0] = extremal;
                    break;
            }
            Name = String.Format("phi.{0}.E{1}", feat, extremal);
            FileInfo = new FileInfo(String.Format("{0}/{1}/weights/{2}.csv", dataDirInfo.FullName, Type, Name));
        }

        public LinearModel(string distribution, string dimension, CMAESData.ObjectiveFunction objFun,
            bool timedependent, DirectoryInfo dataDir)
            : this(null, Features.Mode.Local, 16, 1, !timedependent, distribution, dimension, Model.CMAES)
        {
            string pat = String.Format("full.{0}.{1}.{2}.weights.{3}",
                distribution, dimension, objFun, timedependent ? "timedependent" : "timeindependent");

            DirectoryInfo dir = new DirectoryInfo(String.Format(@"{0}\CMAES\weights", dataDir.FullName));
            Regex reg = new Regex(pat);
            var files = dir.GetFiles("*.csv").Where(path => reg.IsMatch(path.ToString())).ToList();

            if (files.Count <= 0)
                throw new Exception(String.Format("Cannot find any weights belonging to {0}!", pat));

            FileInfo = files[0];

            LinearModel[] w = ReadLoggedLinearWeights(files[0], distribution, dimension, Model.CMAES);
            LocalWeights = w[0].LocalWeights;
        }

        public LinearModel(string distribution, string dimension, TrainingSet.Trajectory track, bool extended,
            PreferenceSet.Ranking rank, bool timedependent, DirectoryInfo dataDir,
            int numFeatures, int modelID, string stepwiseBias, int iter = -1, Features.Mode featMode = Features.Mode.Local)
            : this(null, featMode, numFeatures, modelID, !timedependent, distribution, dimension, Model.PREF)
        {
            switch (track)
            {
                case TrainingSet.Trajectory.ILFIXSUP:
                case TrainingSet.Trajectory.ILUNSUP:
                case TrainingSet.Trajectory.ILSUP:
                    LinearModel model;
                    Iteration = GetImitationLearningFile(out model, distribution, dimension, track, extended,
                        numFeatures, modelID, dataDir.FullName, stepwiseBias, timedependent, iter);
                    FileInfo = model.FileInfo;
                    LocalWeights = model.LocalWeights;
                    return;
                default:
                    string pat = String.Format("\\b(exhaust|full)\\.{0}.{1}.{2}.{3}{4}.{5}.{6}weights.{7}.csv",
                        distribution, dimension, (char) rank,
                        track, extended ? "EXT" : "", stepwiseBias,
                        FeatureMode == Features.Mode.Global ? "(Global|SDR)" : "",
                        timedependent ? "timedependent" : "timeindependent");

                    DirectoryInfo dir = new DirectoryInfo(String.Format(@"{0}\PREF\weights", dataDir.FullName));
                    Regex reg = new Regex(pat);
                    var files = dir.GetFiles("*.csv").Where(path => reg.IsMatch(path.ToString())).ToList();

                    if (files.Count <= 0)
                        throw new Exception(String.Format("Cannot find any weights belonging to {0}!", pat));

                    foreach (var file in files)
                    {
                        LinearModel[] logWeights = ReadLoggedLinearWeights(file, distribution, dimension, Model.PREF);
                        FileInfo = file;

                        foreach (
                            var w in logWeights.Where(w => w._numFeatures == _numFeatures && w._modelID == _modelID))
                        {
                            LocalWeights = w.LocalWeights;
                            GlobalWeights = w.GlobalWeights;
                            return;
                        }
                    }
                    throw new Exception(String.Format("Cannot find weights {0} to user requirements from {1}!", Name,
                        files[0].Name));
            }
        }

        private int GetImitationLearningFile(out LinearModel model, string distribution, string dimension,
            TrainingSet.Trajectory track, bool extended, int numFeatures, int modelID, string directoryName, string stepwiseBias,
            bool timedependent = false, int iter = -1)
        {
            DirectoryInfo dir = new DirectoryInfo(String.Format(@"{0}\PREF\weights", directoryName));

            string pat = String.Format("\\b(exhaust|full)\\.{0}.{1}.{2}.(OPT|IL([0-9]+){3}{4}{5}).{6}.weights.{7}",
                distribution, dimension,
                (char) PreferenceSet.Ranking.PartialPareto, track.ToString().Substring(2),
                numFeatures < Features.LocalCount ? String.Format("_F{0}M{1}", numFeatures, modelID) : "",
                extended ? "EXT" : "",
                stepwiseBias,
                timedependent ? "timedependent" : "timeindependent");

            Regex reg = new Regex(pat);

            var files = dir.GetFiles("*.csv").Where(path => reg.IsMatch(path.ToString())).ToList();

            if (files.Count <= (iter >= 0 ? iter : 0))
                throw new Exception(String.Format("Cannot find any weights belonging to {0}. Start with optimal!", track));

            int[] iters = new int[files.Count];
            for (int i = 0; i < iters.Length; i++)
            {
                Match m = reg.Match(files[i].Name);
                if (m.Groups[2].Value == "OPT")
                    iters[i] = 0;
                else
                    iters[i] = Convert.ToInt32(m.Groups[3].Value);
            }

            if (iter < 0) iter = iters.Max(); // then take the latest version 

            FileInfo weightFile = files[Array.FindIndex(iters, x => x == iter)];
            model = new LinearModel(weightFile, numFeatures, modelID, Distribution, Dimension);

            return iters.Max();
        }

        public double PriorityIndex(Features phi)
        {
            var step = TimeIndependent ? 0 : phi.XiExplanatory[(int) Features.Explanatory.step] - 1;
            double index = 0;

            for (var i = 0; i < Features.LocalCount; i++)
                index += LocalWeights[i][step]*phi.PhiLocal[i];

            if (FeatureMode != Features.Mode.Global) return index;

            for (var i = 0; i < Features.GlobalCount; i++)
                index += GlobalWeights[i][step]*phi.PhiGlobal[i];
            return index;
        }

        public LinearModel()
            : this(new FileInfo("dummy.txt"), Features.Mode.Global, 1, Features.LocalCount + Features.GlobalCount,
                -1, "unity", "dummy", Model.NotSet)
        {
            double[] eps = {WEIGHT_TOLERANCE*2};
            for (var i = 0; i < Features.LocalCount; i++)
                LocalWeights[i] = eps;
            for (var i = 0; i < Features.GlobalCount; i++)
                GlobalWeights[i] = eps;
        }

        public LinearModel(double[][] localWeights, int generation, string distribution, string dimension)
            : this(
                null, Features.Mode.Local, localWeights[0].Length, Features.LocalCount, generation, distribution,
                dimension, Model.CMAES)
        {
            LocalWeights = localWeights;
        }

        public static LinearModel[] GetAllExhaustiveModels(string distribution, string dimension,
            TrainingSet.Trajectory track, int iter, bool extended, PreferenceSet.Ranking rank, bool timedependent,
            DirectoryInfo dataDir, string stepwiseBias)
        {
            string pat = String.Format("exhaust.{0}.{1}.{2}.{3}.{4}.weights.{5}.csv",
                distribution, dimension, (char) rank, Trajectory2String(track, iter, extended),
                stepwiseBias, timedependent ? "timedependent" : "timeindependent");

            DirectoryInfo dir = new DirectoryInfo(String.Format(@"{0}\PREF\weights", dataDir.FullName));
            Regex reg = new Regex(pat);
            var files = dir.GetFiles("*.csv").Where(path => reg.IsMatch(path.ToString())).ToList();

            return files.Count < 1 ? null : ReadLoggedLinearWeights(files[0], distribution, dimension, Model.PREF);
        }

        public static LinearModel[] GetAllVaryLmaxModels(string distribution, string dimension,
            TrainingSet.Trajectory track, int iter, bool extended, PreferenceSet.Ranking rank, bool timedependent,
            DirectoryInfo dataDir, string stepwiseBias)
        {
            string pat = String.Format("full.{0}.{1}.{2}.{3}.{4}.weights.{5}_lmax[0-9]+.csv",
                distribution, dimension, (char) rank, Trajectory2String(track, iter, extended),
                stepwiseBias, timedependent ? "timedependent" : "timeindependent");

            DirectoryInfo dir = new DirectoryInfo(String.Format(@"{0}\PREF\weights", dataDir.FullName));
            Regex reg = new Regex(pat);
            var files = dir.GetFiles("*.csv").Where(path => reg.IsMatch(path.ToString())).ToList();
            if (files.Count < 1) return null;

            LinearModel[] models = new LinearModel[0];
            foreach (
                var model in
                    files.Select(file => ReadLoggedLinearWeights(file, distribution, dimension, Model.PREF))
                        .Where(model => model != null))
            {
                Array.Resize(ref models, models.Length + model.Length);
                Array.Copy(model, 0, models, models.Length - model.Length, model.Length);
            }
            return models;
        }

        private static string Trajectory2String(TrainingSet.Trajectory track, int iter, bool extended)
        {
            string str;
            switch (track)
            {
                case TrainingSet.Trajectory.ILSUP:
                case TrainingSet.Trajectory.ILUNSUP:
                case TrainingSet.Trajectory.ILFIXSUP:
                    str = iter > 0
                        ? String.Format("IL{0}{1}", iter, track.ToString().Substring(2))
                        : String.Format("{0}", TrainingSet.Trajectory.OPT);
                    break;
                default:
                    str = String.Format("{0}", track);
                    break;
            }

            if (extended)
                str += "EXT";

            return str;
        }

        private static LinearModel[] ReadLoggedLinearWeights(FileInfo file, string distribution, string dimension,
            Model model)
        {
            if (!file.Exists)
                throw new Exception(String.Format("File {0} doesn't exist! Cannot read weights.", file.Name));

            bool timeIndependent = Regex.IsMatch(file.Name, "timeindependent");
            Features.Mode featureMode = Regex.IsMatch(file.Name, Features.Mode.Local.ToString())
                ? Features.Mode.Local
                : Features.Mode.Global;

            List<string> header;
            List<string[]> content = CSV.Read(file, out header);

            // 	Weight,NrFeat,Model,Feature,NA,values
            const int WEIGHT = 0;
            const int NRFEAT = 1;
            const int MODEL = 2;
            const int FEATURE = 3;
            const int VALUE = 5;

            var strGlobalFeature = new string[Features.GlobalCount];
            for (var i = 0; i < Features.GlobalCount; i++)
                strGlobalFeature[i] = String.Format("phi.{0}", (Features.Global) i);
            var strLocalFeature = new string[Features.LocalCount];
            for (var i = 0; i < Features.LocalCount; i++)
                strLocalFeature[i] = String.Format("phi.{0}", (Features.Local)i);

            var models = new List<LinearModel>();
            LinearModel linearWeights = null;

            var uniqueTimeSteps = !timeIndependent ? RawData.DimString2Num(dimension) : 1;

            int nrFeat = -1, featFound = -1;
            foreach (var line in content.Where(line => line[WEIGHT].Equals("Weight")))
            {
                if (featFound == nrFeat | featFound == -1)
                {
                    if (linearWeights != null) models.Add(linearWeights);
                    nrFeat = Convert.ToInt32(line[NRFEAT]);
                    var idModel = Convert.ToInt32(line[MODEL]);
                    linearWeights = new LinearModel(file, featureMode, uniqueTimeSteps, nrFeat, idModel, distribution,
                        dimension, model);
                    featFound = 0;
                }

                var phi = line[FEATURE];
                if (timeIndependent) // robust model 
                {
                    var value = Convert.ToDouble(line[VALUE], CultureInfo.InvariantCulture);
                    if (!Char.IsUpper(phi[4]))
                    {
                        for (var i = 0; i < Features.LocalCount; i++)
                        {
                            if (String.Compare(phi, strLocalFeature[i], StringComparison.InvariantCultureIgnoreCase) !=
                                0)
                                continue;
                            if (linearWeights != null) linearWeights.LocalWeights[i][0] = value;
                            featFound++;
                            break;
                        }
                    }
                    else
                    {
                        for (var i = 0; i < Features.GlobalCount; i++)
                        {
                            if (String.Compare(phi, strGlobalFeature[i], StringComparison.InvariantCultureIgnoreCase) !=
                                0)
                                continue;
                            if (linearWeights != null) linearWeights.GlobalWeights[i][0] = value;
                            featFound++;
                            break;
                        }
                    }
                }
                else
                {
                    if (!Char.IsUpper(phi[4]))
                    {
                        for (var i = 0; i < Features.LocalCount; i++)
                        {
                            if (String.Compare(phi, strLocalFeature[i], StringComparison.InvariantCultureIgnoreCase) !=
                                0)
                                continue;
                            for (var step = 0; step < uniqueTimeSteps - 1; step++)
                            {
                                var value = Convert.ToDouble(line[VALUE + step], CultureInfo.InvariantCulture);
                                if (linearWeights != null) linearWeights.LocalWeights[i][step] = value;
                            }
                            featFound++;
                            break;
                        }
                    }
                    else
                    {
                        for (var i = 0; i < Features.GlobalCount; i++)
                        {
                            if (String.Compare(phi, strGlobalFeature[i], StringComparison.InvariantCultureIgnoreCase) !=
                                0)
                                continue;
                            for (var step = 0; step < uniqueTimeSteps - 1; step++)
                            {
                                var value = Convert.ToDouble(line[VALUE + step], CultureInfo.InvariantCulture);
                                if (linearWeights != null) linearWeights.GlobalWeights[i][step] = value;
                            }
                            featFound++;
                            break;
                        }
                    }
                }
            }
            if (linearWeights != null && featFound == nrFeat)
                models.Add(linearWeights);

            int d = Features.LocalCount;
            int minNum = Regex.IsMatch(file.Name, "exhaust")
                ? NChooseK(d, 1) + NChooseK(d, 2) + NChooseK(d, 3) + NChooseK(d, d)
                : 1;

            return models.Count == minNum
                ? models.ToArray()
                : null;
        }

        public static int NChooseK(int n, int k)
        {
            decimal result = 1;
            for (int i = 1; i <= k; i++)
            {
                result *= n - (k - i);
                result /= i;
            }
            return (int) result;
        }
    }
}