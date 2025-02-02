get.trainingDataSize <- function(problems,dim,tracks=c('ALL','CMAESMINRHO','CMAESMINCMAX')){
  get.trainingDataSize1 <- function(problem){
    fname=paste(paste0(DataDir,'Stepwise/size'),'trainingSet',problem,dim,'csv',sep='.')
    if(file.exists(fname)){ stat=read_csv(fname)
    } else {
      trdat <- get.files.TRDAT(problem,dim,tracks)
      if(is.null(trdat)) return(NULL)
      stat=ddply(trdat,~Problem+Step+Track,function(X) nrow(X))
      write.csv(stat, file = fname, row.names = F, quote = F)
    }
    return(stat)
  }
  stats <- as.data.frame(ldply(problems, get.trainingDataSize1))
  stats=factorTrack(stats)
  stats$Problem=factorProblem(stats)
  return(stats)
}

plot.trainingDataSize <- function(trainingDataSize){
  trainingDataSize$Problem <- factorProblem(trainingDataSize,F)
  p=ggplot(trainingDataSize, aes(x=Step,y=V1,color=Track))+
    ggplotColor('Track',num = length(levels(trainingDataSize$Track)))+
    geom_line(size=1,position=position_jitter(w=0.25, h=0))+
    facet_wrap(~Problem,ncol=3)+
    ylab(expression('Size of training set, |' * Phi * '|'))+
    axisStep(trainingDataSize$Dimension[1])+axisCompact +
    guides(color=guide_legend(nrow=2,byrow=TRUE))
  return(p)
}

get.preferenceSetSize <- function(problems,dim,tracks=c('ALL','CMAESMINRHO','CMAESMINCMAX'),
                                  ranks=c('a','b','f','p')){
  get.preferenceSetSize1 <- function(problem,rank){
    fname=paste(paste0(DataDir,'Stepwise/size'),'prefSet',problem,dim,rank,'csv',sep='.')
    if(file.exists(fname)){
      stat=read_csv(fname)
    } else {
      stat <- get.files.TRDAT(problem,dim,tracks,rank,useDiff = T)
      if(is.null(stat)) { return(NULL) }
      stat$Rank=rank
      stat=ddply(stat,~Problem+Step+Rank+Track,function(X) nrow(X))
      write.csv(stat, file = fname, row.names = F, quote = F)
    }
    return(as.data.frame(stat))
  }

  stats <- do.call(rbind, lapply(ranks, function(rank) {
    ldply(problems, get.preferenceSetSize1, rank) } ))

  stats=factorTrack(stats)
  stats$Rank=factorRank(stats$Rank)
  stats$Problem=factorProblem(stats)
  return(stats)
}

plot.preferenceSetSize <- function(preferenceSetSize){
  preferenceSetSize$Problem=factorProblem(preferenceSetSize,F)
  preferenceSetSize$Rank=factorRank(preferenceSetSize$Rank,F)
  p=ggplot(preferenceSetSize, aes(x=Step,y=V1,color=Rank))+
    geom_line(size=1)+
    facet_grid(Problem~Track,scales='free_y')+
    ggplotColor('Ranking',num = 4)+
    ylab(expression('Size of preference set, |' * Psi * '|'))+
    axisStep(preferenceSetSize$Dimension[1])+axisCompact
  return(p)
}

joinRhoSDR <- function(rhoTracksRanks,SDR){
  rhoTracksRanks$Model='PREF'
  if(!is.null(SDR)){
    SDR=subset(SDR, Problem %in% rhoTracksRanks$Problem &
                 Dimension %in% rhoTracksRanks$Dimension & Set %in% rhoTracksRanks$Set)
    SDR$Rank=NA
    SDR$Track=SDR$SDR
    SDR$Model='SDR'
    ix=grepl('CMA',SDR$Track)
    if(any(ix)){ SDR$Model[ix]='MinRho' }

    cols=intersect(names(rhoTracksRanks),names(SDR))
    rhoTracksRanks=rbind(rhoTracksRanks[,cols],SDR[,cols])
  }
  return(rhoTracksRanks)
}

get.CDRTracksRanksComparison <- function(problems,dim,tracks){
  SDR=subset(dataset.SDR,Problem %in% problems & Dimension==dim & SDR%in%tracks)
  if(any(grepl('ES.rho|CMAESMINRHO',tracks))){
      CMA <- get.CDR.CMA(problems, dim, F, 'MinimumRho')
      CMA$SDR = 'ES.rho'
      SDR <- rbind(SDR,CMA[,names(CMA) %in% names(SDR)])
  }
  if(any(grepl('ES.Cmax|CMAESMINCMAX',tracks))){
    CMA <- get.CDR.CMA(problems, dim, F, 'MinimumMakespan')
    CMA$SDR = 'ES.Cmax'
    SDR <- rbind(SDR,CMA[,names(CMA) %in% names(SDR)])
  }
  return(SDR)
}

plot.rhoTracksRanks <- function(rhoTracksRanks,CDR=NULL){

  if(is.null(rhoTracksRanks)){ return(NULL) }

  rhoTracksRanks <- joinRhoSDR(rhoTracksRanks,CDR)
  rhoTracksRanks$Rank <- factorRank(rhoTracksRanks$Rank,F)
  rhoTracksRanks <- factorTrack(rhoTracksRanks)
  rhoTracksRanks$Problem <- factorProblem(rhoTracksRanks,F)

  p <- ggplot(data=rhoTracksRanks , aes(y=Rho, x=Track , fill=Rank)) + geom_boxplot() +
    facet_grid(Problem ~ Track, scale='free')+
    xlab('') + ylab(rhoLabel) + axisCompactY +
    ggplotFill('Ranking',5)

  return(p)

}

table.rhoTracksRanks <- function(problem,rhoTracksRanks,SDR=NULL,save=NA){
  if(is.null(rhoTracksRanks)) return(NULL)
  rhoTracksRanks <- subset(rhoTracksRanks,Problem==problem)
  rhoTracksRanks <- joinRhoSDR(rhoTracksRanks,SDR)
  stat <- ddply(rhoTracksRanks,~Problem+Model+Track+Rank+Set,function(x) summary(x$Rho))
  stat <- arrange(stat, Mean) # order w.r.t. lowest mean
  stat$Problem <- factorProblem(stat, F)
  # table
  lbl <- paste0('stat.pref.',problem)
  tbl <- xtable(stat,label=(lbl),caption=paste('Main statistics for',problem))
  if(is.na(save)) { return(tbl)
  } else {
    print(tbl,include.rownames = FALSE,file=paste(lbl,'.txt',sep=''))
  }
}

stat.trainingDataSize <- function(trainingDataSize){
  trainingDataSize$Problem = factorProblem(trainingDataSize,F)
  mdat=ddply(trainingDataSize,~Problem+Track,function(x) sum(x$V1))
  dcast(mdat,Track~Problem,sum,value.var = 'V1')
}

compare.Baseline <- function(tracks,CDR.full,CDR.compare,rank='p',ReportBetter=T){
  tracks <- setdiff(tracks,c('OPT','ALL'))
  tracks <- factorTrack(tracks)
  CDR.full <- subset(CDR.full,Rank==rank)
  compare1 <- function(track){
    CDR.f <- subset(CDR.full,Track==track);
    CDR.c <- subset(CDR.compare,SDR==track);
    vars=c('Name','Problem','Dimension','Rho')
    CDR=merge(CDR.f[,vars],CDR.c[,vars],by=vars[1:3],suffixes = c('Track','SDR'))
    CDR=melt(CDR,vars[1:3],value.name = 'Rho')
    if(ReportBetter){
      x=better.CDR(CDR,'variable',c('Problem','Dimension','variable'),ID = track)
    } else {
      x=ks.CDR(CDR,'variable',c('Problem','Dimension','variable'))
    }
    colnames(x)[3]=track
    return(x)
  }

  ks <- compare1(tracks[1])
  for(track in tracks[2:length(tracks)]){
    ks <- join(ks,compare1(track),by=c('Problem','Dimension'))
  }

  return(ks)
}

